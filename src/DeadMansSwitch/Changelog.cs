using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DeadMansSwitch
{
    /// <summary>
    /// The changelog built into the app, and a renderer for the small part of
    /// Markdown it uses: "## [1.2] - date" headings, "### Added" subheadings,
    /// "- " bullets (with indented continuation lines), **bold** and `code`.
    /// </summary>
    static class Changelog
    {
        static readonly IBrush Text = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF2));
        static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x90, 0x9A));
        static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
        static readonly IBrush Code = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC));
        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace");

        static readonly Regex Heading = new Regex(@"^##\s+\[?([^\]]+?)\]?(?:\s+-\s+(.*))?$");
        static readonly Regex Inline = new Regex(@"(\*\*[^*]+\*\*|`[^`]+`)");
        static readonly Regex IsDate = new Regex(@"^\d{4}-\d{2}-\d{2}$");

        /// <summary>CHANGELOG.md as of this build, from the newest released version down.</summary>
        public static string Installed()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeadMansSwitch.CHANGELOG.md");
            if (stream == null) return "";
            string text = new StreamReader(stream).ReadToEnd().Replace("\r", "");
            var first = Regex.Match(text, @"(?m)^## \[(?!Unreleased)");
            return first.Success ? text.Substring(first.Index) : text;
        }

        public static void Render(Panel target, string markdown)
        {
            target.Children.Clear();
            TextBlock current = null;   // paragraph or bullet that later lines continue
            bool currentIsBullet = false;

            foreach (var raw in markdown.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("# ")) { current = null; continue; }

                if (line.StartsWith("## "))
                {
                    current = null;
                    var m = Heading.Match(line);
                    var heading = new TextBlock { Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 18, 0, 2), TextWrapping = TextWrapping.Wrap };
                    heading.Inlines.Add(new Run(m.Success ? m.Groups[1].Value : line.Substring(3))
                        { Foreground = Text, FontSize = 16, FontWeight = FontWeight.SemiBold });
                    string note = m.Success ? m.Groups[2].Value : "";
                    if (note.Length > 0)
                        heading.Inlines.Add(new Run("   " + note) { Foreground = IsDate.IsMatch(note) ? Muted : Green, FontSize = 12 });
                    target.Children.Add(heading);
                }
                else if (line.StartsWith("### "))
                {
                    current = null;
                    target.Children.Add(new TextBlock
                    {
                        Text = line.Substring(4).ToUpperInvariant(), Foreground = Muted, FontSize = 11,
                        FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4),
                    });
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    var row = new Grid { Margin = new Thickness(0, 2), ColumnDefinitions = new ColumnDefinitions("14,*") };
                    row.Children.Add(new TextBlock { Text = "•", Foreground = Green, FontSize = 13 });
                    current = NewText();
                    Grid.SetColumn(current, 1);
                    row.Children.Add(current);
                    target.Children.Add(row);
                    currentIsBullet = true;
                    AddInlines(current, line.Substring(2));
                }
                else if (current != null && (!currentIsBullet || raw.StartsWith(" ")))
                {
                    AddInlines(current, " " + line);   // wrapped line of the same paragraph or bullet
                }
                else
                {
                    current = NewText();
                    current.Margin = new Thickness(0, 2, 0, 4);
                    currentIsBullet = false;
                    target.Children.Add(current);
                    AddInlines(current, line);
                }
            }
        }

        static TextBlock NewText() =>
            new TextBlock { Foreground = Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };

        static void AddInlines(TextBlock block, string text)
        {
            foreach (var part in Inline.Split(text))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    block.Inlines.Add(new Run(part.Substring(2, part.Length - 4)) { FontWeight = FontWeight.SemiBold });
                else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
                    block.Inlines.Add(new Run(part.Substring(1, part.Length - 2)) { FontFamily = Mono, Foreground = Code });
                else
                    block.Inlines.Add(new Run(part));
            }
        }
    }
}
