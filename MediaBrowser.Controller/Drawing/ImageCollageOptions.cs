#nullable disable

using System.Collections.Generic;

#pragma warning disable CS1591

namespace MediaBrowser.Controller.Drawing
{
    public class ImageCollageOptions
    {
        /// <summary>
        /// Gets or sets the input paths.
        /// </summary>
        /// <value>The input paths.</value>
        public IReadOnlyList<string> InputPaths { get; set; }

        /// <summary>
        /// Gets or sets the output path.
        /// </summary>
        /// <value>The output path.</value>
        public string OutputPath { get; set; }

        /// <summary>
        /// Gets or sets the width.
        /// </summary>
        /// <value>The width.</value>
        public int Width { get; set; }

        /// <summary>
        /// Gets or sets the height.
        /// </summary>
        /// <value>The height.</value>
        public int Height { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to lay the images out whole in a grid, for logos such as TV
        /// channels', instead of cropping them (Finly).
        /// </summary>
        public bool IsLogoWall { get; set; }
    }
}
