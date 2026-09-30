using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;

namespace Emby.Server.Implementations.Images;

/// <summary>
/// Picks the channels for the Live TV library's logo wall (Finly).
/// </summary>
/// <remarks>
/// The library's image is the same for everyone, so it only shows channels every profile with Live TV can see: those
/// visible to each profile with content restrictions, such as a child's. With no such profiles it shows every TV channel.
/// </remarks>
internal static class LogoWallChannels
{
    /// <summary>
    /// Gets the TV channels with a logo that every restricted profile can see.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <returns>The channels.</returns>
    public static List<BaseItem> GetChannels(ILibraryManager libraryManager, IUserManager userManager)
    {
        var channels = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
            DtoOptions = new DtoOptions(false)
        })
            .Where(i => i is LiveTvChannel { ChannelType: ChannelType.TV }
                && i.GetImageInfo(ImageType.Primary, 0)?.IsLocalFile == true)
            .ToList();

        HashSet<System.Guid>? visibleToAll = null;
        foreach (var user in userManager.GetUsers())
        {
            if (!user.HasPermission(PermissionKind.EnableLiveTvAccess) || !user.HasContentRestrictions())
            {
                continue;
            }

            var visible = libraryManager.GetItemIds(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.LiveTvChannel],
                DtoOptions = new DtoOptions(false)
            });

            if (visibleToAll is null)
            {
                visibleToAll = visible.ToHashSet();
            }
            else
            {
                visibleToAll.IntersectWith(visible);
            }
        }

        return visibleToAll is null
            ? channels
            : channels.Where(c => visibleToAll.Contains(c.Id)).ToList();
    }
}
