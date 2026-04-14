using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using zRover.Core.Tools.Screenshot;

namespace zRover.WinUI.Capabilities
{
    /// <summary>
    /// Reusable helper for searching the XAML VisualTree by name, automationName, type, and text.
    /// Used by tap_element, find_element, hittest, and activate_element tools.
    /// </summary>
    internal static class ElementSearchHelper
    {
        /// <summary>
        /// Describes a matched element with its properties and normalized bounds.
        /// </summary>
        internal sealed class ElementMatch
        {
            public FrameworkElement Element { get; set; } = null!;
            public string Type { get; set; } = "";
            public string? Name { get; set; }
            public string? AutomationName { get; set; }
            public string? Text { get; set; }
            public NormalizedRect Bounds { get; set; } = new NormalizedRect();
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public bool IsVisible { get; set; }
            public bool IsEnabled { get; set; }
        }

        /// <summary>
        /// Criteria for element search. At least one of Name, AutomationName, or TypeName should be set.
        /// </summary>
        internal sealed class SearchCriteria
        {
            /// <summary>Element x:Name (substring match, case-insensitive).</summary>
            public string? Name { get; set; }
            /// <summary>AutomationProperties.Name (substring match, case-insensitive).</summary>
            public string? AutomationName { get; set; }
            /// <summary>XAML type name e.g. "Button", "TextBlock" (exact match, case-insensitive).</summary>
            public string? TypeName { get; set; }
            /// <summary>Parent element name to scope the search under.</summary>
            public string? ParentName { get; set; }
            /// <summary>Text content to match (substring, case-insensitive).</summary>
            public string? Text { get; set; }
            /// <summary>If true, return all matches. Otherwise return first (or at Index).</summary>
            public bool All { get; set; }
            /// <summary>0-based index when multiple elements match. -1 = first match.</summary>
            public int Index { get; set; } = -1;
        }

        /// <summary>
        /// Searches the VisualTree under <paramref name="root"/> for elements matching <paramref name="criteria"/>.
        /// Returns all matching elements with pre-computed centerX/centerY in normalized coordinates.
        /// Must be called on the UI thread.
        /// </summary>
        internal static List<ElementMatch> FindElements(FrameworkElement root, SearchCriteria criteria)
        {
            double winW = root.ActualWidth;
            double winH = root.ActualHeight;
            if (winW <= 0 || winH <= 0)
                return new List<ElementMatch>();

            // If a parent scope is specified, find the parent first
            FrameworkElement searchRoot = root;
            if (!string.IsNullOrEmpty(criteria.ParentName))
            {
                var parent = FindFirstByName(root, criteria.ParentName, root, winW, winH);
                if (parent != null)
                    searchRoot = parent;
            }

            var results = new List<ElementMatch>();
            CollectMatches(searchRoot, root, winW, winH, criteria, results);

            // Apply index filtering
            if (!criteria.All && criteria.Index >= 0 && criteria.Index < results.Count)
                return new List<ElementMatch> { results[criteria.Index] };
            if (!criteria.All && results.Count > 0)
                return new List<ElementMatch> { results[0] };

            return results;
        }

        /// <summary>
        /// Finds the deepest visible element whose bounds contain the given normalized point.
        /// Must be called on the UI thread.
        /// </summary>
        internal static ElementMatch? HitTest(FrameworkElement root, double normalizedX, double normalizedY)
        {
            double winW = root.ActualWidth;
            double winH = root.ActualHeight;
            if (winW <= 0 || winH <= 0) return null;

            ElementMatch? deepest = null;
            HitTestWalk(root, root, winW, winH, normalizedX, normalizedY, ref deepest);
            return deepest;
        }

        /// <summary>
        /// Searches with timeout+polling support. Calls <paramref name="runOnUiThread"/> for each attempt.
        /// </summary>
        internal static async Task<List<ElementMatch>> FindElementsWithTimeoutAsync(
            Func<Func<Task>, Task> runOnUiThread,
            FrameworkElement root,
            SearchCriteria criteria,
            int timeoutMs,
            int pollMs)
        {
            List<ElementMatch> results = null!;
            var deadline = timeoutMs > 0 ? DateTime.UtcNow.AddMilliseconds(timeoutMs) : DateTime.MinValue;

            do
            {
                await runOnUiThread(() =>
                {
                    results = FindElements(root, criteria);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                if (results.Count > 0)
                    return results;

                if (timeoutMs <= 0)
                    break;

                await Task.Delay(Math.Max(50, pollMs)).ConfigureAwait(false);
            } while (DateTime.UtcNow < deadline);

            return results ?? new List<ElementMatch>();
        }

        private static void CollectMatches(
            FrameworkElement element,
            FrameworkElement root,
            double winW, double winH,
            SearchCriteria criteria,
            List<ElementMatch> results)
        {
            if (element.Visibility != Visibility.Visible)
                return;

            if (Matches(element, criteria))
            {
                var match = BuildMatch(element, root, winW, winH);
                if (match != null)
                    results.Add(match);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement childFe)
                    CollectMatches(childFe, root, winW, winH, criteria, results);
            }
        }

        private static bool Matches(FrameworkElement element, SearchCriteria criteria)
        {
            // Type filter (exact, case-insensitive)
            if (!string.IsNullOrEmpty(criteria.TypeName))
            {
                if (!string.Equals(element.GetType().Name, criteria.TypeName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Name filter (substring, case-insensitive)
            if (!string.IsNullOrEmpty(criteria.Name))
            {
                var elName = element.Name;
                if (string.IsNullOrEmpty(elName) ||
                    !elName.Contains(criteria.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // AutomationName filter (substring, case-insensitive)
            if (!string.IsNullOrEmpty(criteria.AutomationName))
            {
                var autoName = AutomationProperties.GetName(element);
                if (string.IsNullOrEmpty(autoName) ||
                    !autoName.Contains(criteria.AutomationName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Text filter (substring, case-insensitive)
            if (!string.IsNullOrEmpty(criteria.Text))
            {
                var text = ExtractText(element);
                if (string.IsNullOrEmpty(text) ||
                    !text.Contains(criteria.Text, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // At least one positive criterion must have been specified
            return !string.IsNullOrEmpty(criteria.Name)
                || !string.IsNullOrEmpty(criteria.AutomationName)
                || !string.IsNullOrEmpty(criteria.TypeName)
                || !string.IsNullOrEmpty(criteria.Text);
        }

        private static ElementMatch? BuildMatch(FrameworkElement element, FrameworkElement root, double winW, double winH)
        {
            NormalizedRect bounds;
            try
            {
                var transform = element.TransformToVisual(root);
                var rect = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                bounds = new NormalizedRect
                {
                    X = winW > 0 ? rect.X / winW : 0,
                    Y = winH > 0 ? rect.Y / winH : 0,
                    Width = winW > 0 ? rect.Width / winW : 0,
                    Height = winH > 0 ? rect.Height / winH : 0
                };
            }
            catch
            {
                return null; // disconnected element
            }

            string? name = string.IsNullOrEmpty(element.Name) ? null : element.Name;
            string? automationName = AutomationProperties.GetName(element);
            if (string.IsNullOrEmpty(automationName)) automationName = null;

            return new ElementMatch
            {
                Element = element,
                Type = element.GetType().Name,
                Name = name,
                AutomationName = automationName,
                Text = ExtractText(element),
                Bounds = bounds,
                CenterX = bounds.X + bounds.Width / 2,
                CenterY = bounds.Y + bounds.Height / 2,
                IsVisible = element.Visibility == Visibility.Visible && element.Opacity > 0,
                IsEnabled = element is Control ctrl ? ctrl.IsEnabled : true
            };
        }

        private static void HitTestWalk(
            FrameworkElement element,
            FrameworkElement root,
            double winW, double winH,
            double nx, double ny,
            ref ElementMatch? deepest)
        {
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0)
                return;

            try
            {
                var transform = element.TransformToVisual(root);
                var rect = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                double ex = winW > 0 ? rect.X / winW : 0;
                double ey = winH > 0 ? rect.Y / winH : 0;
                double ew = winW > 0 ? rect.Width / winW : 0;
                double eh = winH > 0 ? rect.Height / winH : 0;

                if (nx >= ex && nx <= ex + ew && ny >= ey && ny <= ey + eh)
                {
                    // This element contains the point — record it as the current deepest
                    deepest = BuildMatch(element, root, winW, winH);

                    // Continue into children to find deeper matches
                    int childCount = VisualTreeHelper.GetChildrenCount(element);
                    for (int i = 0; i < childCount; i++)
                    {
                        if (VisualTreeHelper.GetChild(element, i) is FrameworkElement childFe)
                            HitTestWalk(childFe, root, winW, winH, nx, ny, ref deepest);
                    }
                }
            }
            catch { /* disconnected or zero-size element */ }
        }

        private static FrameworkElement? FindFirstByName(FrameworkElement element, string name, FrameworkElement root, double winW, double winH)
        {
            if (element.Visibility != Visibility.Visible)
                return null;

            var elName = element.Name;
            if (!string.IsNullOrEmpty(elName) && elName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return element;

            var autoName = AutomationProperties.GetName(element);
            if (!string.IsNullOrEmpty(autoName) && autoName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return element;

            int childCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement childFe)
                {
                    var found = FindFirstByName(childFe, name, root, winW, winH);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private static string? ExtractText(FrameworkElement element)
        {
            if (element is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                return tb.Text;
            if (element is TextBox txb && !string.IsNullOrEmpty(txb.Text))
                return txb.Text;
            if (element is ContentControl cc && cc.Content is string s && !string.IsNullOrEmpty(s))
                return s;
            return null;
        }
    }
}
