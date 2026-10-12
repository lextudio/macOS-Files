// Copyright (c) Files Community
// Licensed under the MIT License.

using ColorCode;
using Files.App.ViewModels.Previews;
using Markdig;
using Markdig.Syntax;
using Block = Markdig.Syntax.Block;
using Markdig.Syntax.Inlines;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Files.App.UserControls.FilePreviews
{
	/// <summary>
	/// Turns untrusted text into inert XAML inlines/blocks: syntax highlighted code and basic Markdown.
	/// Links are rendered as underlined text only (never navigated), raw HTML is shown as literal text and images are not loaded.
	/// </summary>
	public static class PreviewTextRenderer
	{
		/// <summary>
		/// Highlighting is skipped above this size to keep the UI responsive.
		/// </summary>
		private const int MaxHighlightChars = 96 * 1024;

		private const int MaxMarkdownChars = 128 * 1024;

		private static readonly MarkdownPipeline markdownPipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

		public static FontFamily GetMonospaceFont()
		{
			if (Application.Current?.Resources.TryGetValue("Files.Desktop.MonospaceFontFamily", out var value) == true && value is FontFamily family)
				return family;

			return new FontFamily("Consolas");
		}

		public sealed record Model(string Text, MarkdownDocument? Markdown, List<PreviewCodeTokenizer.Token>? Tokens);

		// This model contains no XAML objects and can be reused when the theme changes.
		public static Model Parse(string text, TextPreviewKind kind, ILanguage? language, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();
			try
			{
				MarkdownDocument? markdown = null;
				List<PreviewCodeTokenizer.Token>? tokens = null;
				if (kind == TextPreviewKind.Markdown && text.Length <= MaxMarkdownChars)
					markdown = Markdown.Parse(text, markdownPipeline);
				else if (kind == TextPreviewKind.Code && language is not null && text.Length <= MaxHighlightChars)
					tokens = new PreviewCodeTokenizer().Tokenize(text, language, token);
				token.ThrowIfCancellationRequested();
				return new Model(text, markdown, tokens);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception)
			{
				return new Model(text, null, null);
			}
		}

		public static void AddPlainInlines(InlineCollection target, string text, SolidColorBrush? foreground = null)
		{
			const int chunkSize = 4096;
			for (var start = 0; start < text.Length;)
			{
				var length = Math.Min(chunkSize, text.Length - start);
				if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
					length--;
				var run = new Run { Text = text.Substring(start, length) };
				if (foreground is not null)
					run.Foreground = foreground;
				target.Add(run);
				start += length;
			}
		}

		public static void AddCodeInlines(Paragraph paragraph, Model model, bool dark)
		{
			if (model.Tokens is null)
			{
				AddPlainInlines(paragraph.Inlines, model.Text);
				return;
			}
			foreach (var token in model.Tokens)
			{
				var brush = GetColor(token.ScopeName, dark) is { } color ? new SolidColorBrush(color) : null;
				AddPlainInlines(paragraph.Inlines, token.Text, brush);
			}
		}

		private static Color? GetColor(string? scopeName, bool dark)
		{
			if (string.IsNullOrEmpty(scopeName))
				return null;

			if (scopeName.Contains("Comment", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 106, 153, 85) : Color.FromArgb(255, 0, 128, 0);
			if (scopeName.Contains("String", StringComparison.OrdinalIgnoreCase) || scopeName.Contains("AttributeValue", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 206, 145, 120) : Color.FromArgb(255, 163, 21, 21);
			if (scopeName.Contains("Keyword", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 86, 156, 214) : Color.FromArgb(255, 0, 0, 255);
			if (scopeName.Contains("Number", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 181, 206, 168) : Color.FromArgb(255, 9, 134, 88);
			if (scopeName.Contains("ClassName", StringComparison.OrdinalIgnoreCase) || scopeName.Contains("ElementName", StringComparison.OrdinalIgnoreCase)
				|| scopeName.Contains("XmlName", StringComparison.OrdinalIgnoreCase) || scopeName.Contains("Type", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 78, 201, 176) : Color.FromArgb(255, 43, 145, 175);
			if (scopeName.Contains("Attribute", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 156, 220, 254) : Color.FromArgb(255, 255, 0, 0);
			if (scopeName.Contains("Delimiter", StringComparison.OrdinalIgnoreCase) || scopeName.Contains("Operator", StringComparison.OrdinalIgnoreCase))
				return dark ? Color.FromArgb(255, 128, 128, 128) : Color.FromArgb(255, 128, 128, 128);

			return null;
		}

		public static void AddMarkdownBlocks(BlockCollection blocks, Model model, bool dark)
		{
			if (model.Markdown is null)
			{
				blocks.Add(PlainParagraph(model.Text));
				return;
			}
			try
			{
				foreach (var block in model.Markdown)
					AddBlock(blocks, block, 0, dark);
			}
			catch (Exception)
			{
				blocks.Clear();
				blocks.Add(PlainParagraph(model.Text));
			}
		}

		private static Paragraph PlainParagraph(string text)
		{
			var paragraph = new Paragraph();
			AddPlainInlines(paragraph.Inlines, text);
			return paragraph;
		}

		private static void AddBlock(BlockCollection blocks, Block block, int depth, bool dark)
		{
			if (depth >= 32 || blocks.Count >= 2048)
				throw new System.IO.InvalidDataException("The Markdown preview limit was exceeded.");

			var indent = new Thickness(depth * 20, 0, 0, 8);

			switch (block)
			{
				case HeadingBlock heading:
				{
					var paragraph = new Paragraph
					{
						FontWeight = FontWeights.SemiBold,
						FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 },
						Margin = new Thickness(depth * 20, heading.Level <= 2 ? 8 : 4, 0, 6),
					};
					AddInlines(paragraph.Inlines, heading.Inline, dark);
					blocks.Add(paragraph);
					break;
				}
				case ParagraphBlock paragraphBlock:
				{
					var paragraph = new Paragraph { Margin = indent };
					AddInlines(paragraph.Inlines, paragraphBlock.Inline, dark);
					blocks.Add(paragraph);
					break;
				}
				case ListBlock list:
				{
					var number = 1;
					if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start))
						number = start;

					foreach (var item in list.OfType<ListItemBlock>())
					{
						var marker = list.IsOrdered ? $"{number++}. " : "• ";
						var first = true;
						foreach (var child in item)
						{
							if (first && child is ParagraphBlock itemParagraph)
							{
								var paragraph = new Paragraph { Margin = new Thickness(depth * 20 + 8, 0, 0, 2) };
								paragraph.Inlines.Add(new Run { Text = marker });
								AddInlines(paragraph.Inlines, itemParagraph.Inline, dark);
								blocks.Add(paragraph);
							}
							else
							{
								AddBlock(blocks, child, depth + 1, dark);
							}
							first = false;
						}
					}
					break;
				}
				case QuoteBlock quote:
				{
					var firstIndex = blocks.Count;
					foreach (var child in quote)
						AddBlock(blocks, child, depth + 1, dark);

					for (var i = firstIndex; i < blocks.Count; i++)
					{
						if (blocks[i] is Paragraph p)
						{
							p.FontStyle = Windows.UI.Text.FontStyle.Italic;
							p.Inlines.Insert(0, new Run { Text = "\u258E " });
						}
					}
					break;
				}
				case CodeBlock code:
				{
					var paragraph = new Paragraph
					{
						FontFamily = GetMonospaceFont(),
						FontSize = 12,
						Margin = new Thickness(depth * 20 + 8, 0, 0, 8),
					};
					AddPlainInlines(paragraph.Inlines, string.Join('\n', code.Lines.Lines.Select(l => l.ToString()).Take(code.Lines.Count)).TrimEnd('\n'));
					blocks.Add(paragraph);
					break;
				}
				case ThematicBreakBlock:
					blocks.Add(PlainParagraph(new string('─', 24)));
					break;
				case LeafBlock leaf:
				{
					// Raw HTML and anything else: literal text only.
					var paragraph = new Paragraph { Margin = indent, FontFamily = GetMonospaceFont(), FontSize = 12 };
					AddPlainInlines(paragraph.Inlines, leaf.Lines.ToString());
					blocks.Add(paragraph);
					break;
				}
				case ContainerBlock container:
					foreach (var child in container)
						AddBlock(blocks, child, depth, dark);
					break;
			}
		}

		private static void AddInlines(InlineCollection target, ContainerInline? container, bool dark, int depth = 0)
		{
			if (container is null)
				return;
			if (depth >= 32)
				throw new System.IO.InvalidDataException("The Markdown preview limit was exceeded.");

			foreach (var inline in container)
			{
				if (target.Count >= 2048)
					throw new System.IO.InvalidDataException("The Markdown preview limit was exceeded.");
				switch (inline)
				{
					case LiteralInline literal:
						AddPlainInlines(target, literal.Content.ToString());
						break;
					case LineBreakInline lineBreak:
						if (lineBreak.IsHard)
							target.Add(new LineBreak());
						else
							target.Add(new Run { Text = " " });
						break;
					case CodeInline code:
						target.Add(new Run { Text = code.Content, FontFamily = GetMonospaceFont() });
						break;
					case EmphasisInline emphasis:
					{
						Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
						AddInlines(span.Inlines, emphasis, dark, depth + 1);
						target.Add(span);
						break;
					}
					case LinkInline link when link.IsImage:
					{
						var alt = new Span();
						alt.Inlines.Add(new Run { Text = "[" });
						AddInlines(alt.Inlines, link, dark, depth + 1);
						alt.Inlines.Add(new Run { Text = "]" });
						target.Add(alt);
						break;
					}
					case LinkInline link:
					{
						// Rendered as text only; links are never opened from the preview.
						var span = new Underline();
						span.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 117, 190, 255) : Color.FromArgb(255, 0, 90, 180));
						AddInlines(span.Inlines, link, dark, depth + 1);
						target.Add(span);
						break;
					}
					case AutolinkInline auto:
					{
						var span = new Underline();
						span.Foreground = new SolidColorBrush(dark ? Color.FromArgb(255, 117, 190, 255) : Color.FromArgb(255, 0, 90, 180));
						span.Inlines.Add(new Run { Text = auto.Url });
						target.Add(span);
						break;
					}
					case HtmlInline html:
						target.Add(new Run { Text = html.Tag });
						break;
					case ContainerInline nested:
						AddInlines(target, nested, dark, depth + 1);
						break;
				}
			}
		}
	}
}

