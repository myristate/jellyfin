#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Text;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.Listings
{
    internal class EpgChannelData
    {
        private readonly Dictionary<string, ChannelInfo> _channelsById;

        private readonly Dictionary<string, ChannelInfo> _channelsByNumber;

        private readonly Dictionary<string, ChannelInfo> _channelsByExactName;

        private readonly Dictionary<string, ChannelInfo> _channelsByName;

        public EpgChannelData(IEnumerable<ChannelInfo> channels)
        {
            _channelsById = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByNumber = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByExactName = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);
            _channelsByName = new Dictionary<string, ChannelInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var channel in channels)
            {
                _channelsById[channel.Id] = channel;

                if (!string.IsNullOrEmpty(channel.Number))
                {
                    _channelsByNumber[channel.Number] = channel;
                }

                var exactName = ExactName(channel.Name ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(exactName))
                {
                    _channelsByExactName[exactName] = channel;
                }

                // (Finly) The first guide channel with a name keeps it, so "Film4" stays "Film4" rather than "Film4 HD"
                var normalizedName = NormalizeName(channel.Name ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    _channelsByName.TryAdd(normalizedName, channel);
                }
            }
        }

        public ChannelInfo? GetChannelById(string id)
            => _channelsById.GetValueOrDefault(id);

        public ChannelInfo? GetChannelByNumber(string number)
            => _channelsByNumber.GetValueOrDefault(number);

        /// <summary>
        /// Finds the guide channel for a tuner channel's name: an exact match (ignoring case, spaces and hyphens, as
        /// Jellyfin always has) wins, then the looser <see cref="NormalizeName"/> match (Finly).
        /// </summary>
        /// <param name="name">The tuner channel's name, as it is.</param>
        /// <returns>The guide channel, or <c>null</c>.</returns>
        public ChannelInfo? GetChannelByName(string name)
            => _channelsByExactName.GetValueOrDefault(ExactName(name)) ?? _channelsByName.GetValueOrDefault(NormalizeName(name));

        private static string ExactName(string value)
            => value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);

        /// <summary>
        /// Reduces a channel name to what identifies it, so a tuner's name matches the guide's (Finly): case, spaces,
        /// punctuation and a separate "HD" don't count, so "BBC NEWS" matches "BBC NEWS HD", "CBBC HD" matches "CBBC" and
        /// "Hobby Maker" matches "HobbyMaker". "+1" stays, so a timeshift channel never takes its parent's listings.
        /// </summary>
        /// <param name="value">The channel name.</param>
        /// <returns>The name to match on.</returns>
        public static string NormalizeName(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Equals("HD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var c in word)
                {
                    if (char.IsLetterOrDigit(c) || c == '+')
                    {
                        builder.Append(char.ToLowerInvariant(c));
                    }
                }
            }

            return builder.ToString();
        }
    }
}
