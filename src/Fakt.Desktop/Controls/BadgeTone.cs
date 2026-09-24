namespace Fakt.Desktop.Controls
{
    /// <summary>
    /// Semantic tone of a <see cref="StatusBadge"/> (Arctic Blue, DESIGN.md §6.6).
    /// Colour only supplements the badge text; it never replaces it.
    /// </summary>
    public enum BadgeTone
    {
        /// <summary>Neutral information: Slate text on Blue.50.</summary>
        Neutral,

        /// <summary>Informational / in-progress: Ocean.700 text on Blue.100.</summary>
        Info,

        /// <summary>Success: Green.700 text on Green.50.</summary>
        Success,

        /// <summary>Warning / needs attention: Amber.800 text on Amber.50.</summary>
        Warning,

        /// <summary>Error / blocking problem: Red.700 text on Red.50.</summary>
        Error
    }
}
