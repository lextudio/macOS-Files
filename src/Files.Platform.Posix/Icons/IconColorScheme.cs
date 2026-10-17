// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.Platform.Posix.Icons
{
	/// <summary>
	/// The palette substituted into themed SVG icons that paint with <c>currentColor</c> through the
	/// <c>current-color-scheme</c> stylesheet (Breeze and derivatives), as KIconLoader does for KDE applications.
	/// </summary>
	public sealed record IconColorScheme(
		string Text,
		string Background,
		string Highlight,
		string HighlightedText,
		string PositiveText,
		string NeutralText,
		string NegativeText,
		string ActiveText,
		string Complement,
		string Contrast)
	{
		/// <summary>Breeze Light colours.</summary>
		public static IconColorScheme Light { get; } = new("#232629", "#fcfcfc", "#3daee9", "#fcfcfc", "#27ae60", "#f67400", "#da4453", "#3daee9", "#31363b", "#fcfcfc");

		/// <summary>Breeze Dark colours.</summary>
		public static IconColorScheme Dark { get; } = new("#fcfcfc", "#232629", "#3daee9", "#fcfcfc", "#27ae60", "#f67400", "#da4453", "#3daee9", "#fcfcfc", "#232629");

		/// <summary>
		/// Gets the scheme matching the application theme.
		/// </summary>
		public static IconColorScheme ForTheme(bool isDark) => isDark ? Dark : Light;

		/// <summary>
		/// Builds the stylesheet KIconLoader injects, covering every <c>ColorScheme-*</c> class Breeze icons use.
		/// </summary>
		public string ToStyleSheet() =>
			$".ColorScheme-Text {{ color:{Text}; }} .ColorScheme-Background {{ color:{Background}; }} .ColorScheme-Highlight {{ color:{Highlight}; }} " +
			$".ColorScheme-HighlightedText {{ color:{HighlightedText}; }} .ColorScheme-PositiveText {{ color:{PositiveText}; }} .ColorScheme-NeutralText {{ color:{NeutralText}; }} " +
			$".ColorScheme-NegativeText {{ color:{NegativeText}; }} .ColorScheme-ActiveText {{ color:{ActiveText}; }} .ColorScheme-Complement {{ color:{Complement}; }} " +
			$".ColorScheme-Contrast {{ color:{Contrast}; }} .ColorScheme-Accent {{ color:{Highlight}; }}";
	}
}
