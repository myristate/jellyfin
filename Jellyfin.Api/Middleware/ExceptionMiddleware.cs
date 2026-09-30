using System;
using System.IO;
using System.Net.Mime;
using System.Net.Sockets;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Exception Middleware.
/// </summary>
public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;
    private readonly IServerConfigurationManager _configuration;
    private readonly IWebHostEnvironment _hostEnvironment;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExceptionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next request delegate.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ExceptionMiddleware}"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="hostEnvironment">Instance of the <see cref="IWebHostEnvironment"/> interface.</param>
    public ExceptionMiddleware(
        RequestDelegate next,
        ILogger<ExceptionMiddleware> logger,
        IServerConfigurationManager serverConfigurationManager,
        IWebHostEnvironment hostEnvironment)
    {
        _next = next;
        _logger = logger;
        _configuration = serverConfigurationManager;
        _hostEnvironment = hostEnvironment;
    }

    /// <summary>
    /// Invoke request.
    /// </summary>
    /// <param name="context">Request context.</param>
    /// <returns>Task.</returns>
    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                _logger.LogWarning("The response has already started, the exception middleware will not be executed.");
                throw;
            }

            ex = GetActualException(ex);

            bool ignoreStackTrace =
                ex is SocketException
                || ex is IOException
                || ex is OperationCanceledException
                || ex is SecurityException
                || ex is AuthenticationException
                || ex is FileNotFoundException
                || IsExpectedLiveTvError(ex);

            if (ignoreStackTrace)
            {
                _logger.LogError(
                    "Error processing request: {ExceptionMessage}. URL {Method} {Url}.",
                    ex.Message.TrimEnd('.'),
                    context.Request.Method,
                    context.Request.Path);
            }
            else
            {
                _logger.LogError(
                    ex,
                    "Error processing request. URL {Method} {Url}.",
                    context.Request.Method,
                    context.Request.Path);
            }

            context.Response.StatusCode = GetStatusCode(ex);
            context.Response.ContentType = MediaTypeNames.Text.Plain;
            AddLiveTvHeaders(context.Response, ex);

            // Don't send exception unless the server is in a Development environment
            var errorContent = _hostEnvironment.IsDevelopment()
                    ? NormalizeExceptionMessage(ex.Message)
                    : "Error processing request.";
            await context.Response.WriteAsync(errorContent).ConfigureAwait(false);
        }
    }

    private static Exception GetActualException(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            var inner = agg.InnerException;
            if (inner is not null)
            {
                return GetActualException(inner);
            }

            var inners = agg.InnerExceptions;
            if (inners.Count > 0)
            {
                return GetActualException(inners[0]);
            }
        }

        return ex;
    }

    internal static int GetStatusCode(Exception ex)
    {
        return ex switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            AuthenticationException => StatusCodes.Status401Unauthorized,
            SecurityException => StatusCodes.Status403Forbidden,
            DirectoryNotFoundException => StatusCodes.Status404NotFound,
            FileNotFoundException => StatusCodes.Status404NotFound,
            ResourceNotFoundException => StatusCodes.Status404NotFound,
            MethodNotAllowedException => StatusCodes.Status405MethodNotAllowed,
            // (Finly) The Android TV app and web take a 503 to mean the channel isn't broadcasting. A busy tuner is a
            // conflict, a tuner that failed a bad gateway, or a gateway timeout when it didn't answer in time.
            LiveTvChannelUnavailableException => StatusCodes.Status503ServiceUnavailable,
            LiveTvConflictException => StatusCodes.Status409Conflict,
            LiveTvTunerException { IsTimeout: true } => StatusCodes.Status504GatewayTimeout,
            LiveTvTunerException => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError
        };
    }

    /// <summary>
    /// Gets whether an error is an expected Live TV outcome, such as a busy tuner, logged without a stack trace (Finly).
    /// </summary>
    /// <param name="ex">The error.</param>
    /// <returns>Whether it is expected.</returns>
    internal static bool IsExpectedLiveTvError(Exception ex)
        => ex is LiveTvChannelUnavailableException or LiveTvConflictException or LiveTvTunerException;

    /// <summary>
    /// Says why a Live TV channel couldn't be played, in the X-Finly-Reason header (Finly).
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="ex">The error.</param>
    internal static void AddLiveTvHeaders(HttpResponse response, Exception ex)
    {
        var reason = ex switch
        {
            LiveTvChannelUnavailableException => "NotBroadcasting",
            LiveTvConflictException => "TunersBusy",
            LiveTvTunerException { IsTimeout: true } => "TunerTimeout",
            LiveTvTunerException => "TunerFailed",
            _ => null
        };

        if (reason is not null)
        {
            response.Headers["X-Finly-Reason"] = reason;
        }

        if (ex is LiveTvConflictException)
        {
            response.Headers.RetryAfter = "5";
        }
    }

    private string NormalizeExceptionMessage(string msg)
    {
        // Strip any information we don't want to reveal
        return msg.Replace(
                _configuration.ApplicationPaths.ProgramSystemPath,
                string.Empty,
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                _configuration.ApplicationPaths.ProgramDataPath,
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
    }
}
