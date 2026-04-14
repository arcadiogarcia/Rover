using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using zRover.Core;
using zRover.Core.Tools.Screenshot;
using zRover.Core.Tools.InputInjection;
using zRover.Core.Tools.UiTree;
using System.Linq;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace zRover.WinUI.Capabilities
{
    /// <summary>
    /// Exposes the XAML visual tree as a JSON hierarchy via the <c>get_ui_tree</c> MCP tool.
    /// Each node includes element type, x:Name, AutomationProperties.Name, text content,
    /// normalized bounds (0–1 relative to the app window), visibility, and enabled state.
    /// </summary>
    internal sealed class UiTreeCapability : IDebugCapability
    {
        private DebugHostContext? _context;
        private readonly Microsoft.UI.Xaml.Window _window;

        public string Name => "UiTree";

        public UiTreeCapability(Microsoft.UI.Xaml.Window window)
        {
            _window = window;
        }

        public Task StartAsync(DebugHostContext context)
        {
            _context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _context = null;
            return Task.CompletedTask;
        }

        private const string Schema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""maxDepth"": { ""type"": ""integer"", ""default"": 32, ""description"": ""Maximum depth to traverse. Reduce to limit output for large trees."" },
    ""visibleOnly"": { ""type"": ""boolean"", ""default"": false, ""description"": ""When true, skips elements where Visibility != Visible."" }
  }
}";

        public void RegisterTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "get_ui_tree",
                "**Primary tool for understanding UI layout and locating click targets.** " +
                "Returns the XAML visual tree as a JSON hierarchy. Each node includes: " +
                "'type' (XAML class name e.g. Button, TextBlock, Grid), " +
                "'name' (x:Name), 'automationName' (AutomationProperties.Name), " +
                "'text' (text content for TextBlock/TextBox/ContentControl), " +
                "'bounds' (normalized 0.0–1.0 rect relative to the app window — same coordinate space as inject_tap), " +
                "'isVisible', 'isEnabled', and 'children'. " +
                "To click an element: compute its center from bounds (centerX = bounds.x + bounds.width/2, centerY = bounds.y + bounds.height/2) and pass to inject_tap. " +
                "For simpler targeting, use tap_element (clicks by name/type directly) or find_element (returns pre-computed centerX/centerY). " +
                "This tree-based approach is FAR more reliable than estimating pixel coordinates from screenshots.",
                Schema,
                GetUiTreeAsync);

            registry.RegisterTool(
                "find_element",
                "Searches the XAML visual tree for elements matching the given criteria and returns their exact coordinates. " +
                "Returns pre-computed centerX/centerY in normalized coordinates (0.0-1.0) — pass these directly to inject_tap with no math needed. " +
                "This is MORE RELIABLE than estimating coordinates from screenshots. " +
                "Supports searching by name (x:Name), automationName (AutomationProperties.Name), type (e.g. 'Button'), and text content. " +
                "Use 'parent' to scope the search. Use 'timeout' to wait for dynamically appearing elements. " +
                "Use 'all' to get all matches. For even simpler interaction, use tap_element which finds AND clicks in one step.",
                ToolSchemas.FindElementSchema,
                FindElementAsync);

            registry.RegisterTool(
                "hittest",
                "Returns the UI element at the specified coordinates. " +
                "Use this to verify what element is at a position BEFORE clicking — much more informative than validate_position which only draws a crosshair. " +
                "Returns the element's name, type, automationName, text, bounds, and pre-computed centerX/centerY. " +
                "Useful for confirming that your estimated coordinates actually land on the intended element.",
                ToolSchemas.HitTestSchema,
                HitTestAsync);
        }

        private async Task<string> GetUiTreeAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<UiTreeRequest>(
                    string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson)
                    ?? new UiTreeRequest();

                UiTreeNode? root = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(() =>
                    {
                        root = BuildTree(req);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    root = BuildTree(req);
                }

                return JsonConvert.SerializeObject(new UiTreeResponse { Success = true, Root = root });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new UiTreeResponse { Success = false, Error = ex.Message });
            }
        }

        private UiTreeNode? BuildTree(UiTreeRequest req)
        {
            var windowContent = _window.Content as FrameworkElement;
            if (windowContent == null)
                return null;

            double winW = windowContent.ActualWidth;
            double winH = windowContent.ActualHeight;
            return WalkElement(windowContent, windowContent, winW, winH, 0, req.MaxDepth, req.VisibleOnly);
        }

        private static UiTreeNode? WalkElement(
            FrameworkElement element,
            FrameworkElement root,
            double winW,
            double winH,
            int depth,
            int maxDepth,
            bool visibleOnly)
        {
            if (visibleOnly && element.Visibility != Visibility.Visible)
                return null;

            var bounds = new NormalizedRect();
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
            catch { /* disconnected or zero-size element — leave bounds at zero */ }

            string? name = string.IsNullOrEmpty(element.Name) ? null : element.Name;
            string? automationName = AutomationProperties.GetName(element);
            if (string.IsNullOrEmpty(automationName)) automationName = null;

            bool isVisible = element.Visibility == Visibility.Visible && element.Opacity > 0;
            bool isEnabled = element is Control ctrl ? ctrl.IsEnabled : true;

            var node = new UiTreeNode
            {
                Type = element.GetType().Name,
                Name = name,
                AutomationName = automationName,
                Text = ExtractText(element),
                Bounds = bounds,
                IsVisible = isVisible,
                IsEnabled = isEnabled,
                Children = new List<UiTreeNode>()
            };

            if (depth < maxDepth)
            {
                int childCount = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < childCount; i++)
                {
                    if (VisualTreeHelper.GetChild(element, i) is FrameworkElement childFe)
                    {
                        var childNode = WalkElement(childFe, root, winW, winH, depth + 1, maxDepth, visibleOnly);
                        if (childNode != null)
                            node.Children.Add(childNode);
                    }
                }
            }

            return node;
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

        private async Task<string> FindElementAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<FindElementRequest>(
                    string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson)
                    ?? new FindElementRequest();

                if (string.IsNullOrEmpty(req.Name) && string.IsNullOrEmpty(req.AutomationName) 
                    && string.IsNullOrEmpty(req.TypeName) && string.IsNullOrEmpty(req.Text))
                    return JsonConvert.SerializeObject(new FindElementResponse { Found = false, Error = "At least one of 'name', 'automationName', 'type', or 'text' is required." });

                var criteria = new ElementSearchHelper.SearchCriteria
                {
                    Name = req.Name,
                    AutomationName = req.AutomationName,
                    TypeName = req.TypeName,
                    ParentName = req.Parent,
                    Text = req.Text,
                    All = req.All,
                    Index = req.Index
                };

                // All XAML property access (including _window.Content) must happen on the UI thread
                List<ElementSearchHelper.ElementMatch> matches;
                if (_context!.RunOnUiThread != null)
                {
                    var deadline = req.Timeout > 0 ? DateTime.UtcNow.AddMilliseconds(req.Timeout) : DateTime.MinValue;
                    matches = new List<ElementSearchHelper.ElementMatch>();
                    do
                    {
                        await _context.RunOnUiThread(() =>
                        {
                            var windowContent = _window.Content as FrameworkElement;
                            if (windowContent != null)
                                matches = ElementSearchHelper.FindElements(windowContent, criteria);
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                        if (matches.Count > 0) break;
                        if (req.Timeout <= 0) break;
                        await Task.Delay(Math.Max(50, req.Poll)).ConfigureAwait(false);
                    } while (DateTime.UtcNow < deadline);
                }
                else
                {
                    var windowContent = _window.Content as FrameworkElement;
                    matches = windowContent != null
                        ? ElementSearchHelper.FindElements(windowContent, criteria)
                        : new List<ElementSearchHelper.ElementMatch>();
                }

                if (matches.Count == 0)
                    return JsonConvert.SerializeObject(new FindElementResponse { Found = false, MatchCount = 0 });

                if (req.All)
                {
                    var allMatches = matches.Select(m => new FindElementMatch
                    {
                        Name = m.Name,
                        Type = m.Type,
                        AutomationName = m.AutomationName,
                        Text = m.Text,
                        CenterX = m.CenterX,
                        CenterY = m.CenterY,
                        Bounds = m.Bounds,
                        IsVisible = m.IsVisible,
                        IsEnabled = m.IsEnabled
                    }).ToList();

                    return JsonConvert.SerializeObject(new FindElementResponse
                    {
                        Found = true,
                        MatchCount = allMatches.Count,
                        Matches = allMatches
                    });
                }

                var first = matches[0];
                return JsonConvert.SerializeObject(new FindElementResponse
                {
                    Found = true,
                    Name = first.Name,
                    Type = first.Type,
                    AutomationName = first.AutomationName,
                    Text = first.Text,
                    CenterX = first.CenterX,
                    CenterY = first.CenterY,
                    Bounds = first.Bounds,
                    IsVisible = first.IsVisible,
                    IsEnabled = first.IsEnabled,
                    MatchCount = matches.Count > 1 ? matches.Count : 1
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new FindElementResponse { Found = false, Error = ex.Message });
            }
        }

        private async Task<string> HitTestAsync(string argsJson)
        {
            try
            {
                var req = JsonConvert.DeserializeObject<HitTestRequest>(
                    string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson)
                    ?? new HitTestRequest();

                double nx = req.X, ny = req.Y;

                // All XAML property access must happen on the UI thread
                ElementSearchHelper.ElementMatch? match = null;
                if (_context!.RunOnUiThread != null)
                {
                    await _context.RunOnUiThread(() =>
                    {
                        var windowContent = _window.Content as FrameworkElement;
                        if (windowContent == null) return Task.CompletedTask;

                        // If pixels, convert to normalized
                        if (string.Equals(req.CoordinateSpace, "pixels", StringComparison.OrdinalIgnoreCase))
                        {
                            double winW = windowContent.ActualWidth;
                            double winH = windowContent.ActualHeight;
                            if (winW > 0) nx = req.X / winW;
                            if (winH > 0) ny = req.Y / winH;
                        }

                        match = ElementSearchHelper.HitTest(windowContent, nx, ny);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    var windowContent = _window.Content as FrameworkElement;
                    if (windowContent != null)
                    {
                        if (string.Equals(req.CoordinateSpace, "pixels", StringComparison.OrdinalIgnoreCase))
                        {
                            double winW = windowContent.ActualWidth;
                            double winH = windowContent.ActualHeight;
                            if (winW > 0) nx = req.X / winW;
                            if (winH > 0) ny = req.Y / winH;
                        }
                        match = ElementSearchHelper.HitTest(windowContent, nx, ny);
                    }
                }

                if (match == null)
                    return JsonConvert.SerializeObject(new HitTestResponse { Success = false, Error = "No element found at the specified point." });

                return JsonConvert.SerializeObject(new HitTestResponse
                {
                    Success = true,
                    Type = match.Type,
                    Name = match.Name,
                    AutomationName = match.AutomationName,
                    Text = match.Text,
                    CenterX = match.CenterX,
                    CenterY = match.CenterY,
                    Bounds = match.Bounds,
                    IsVisible = match.IsVisible,
                    IsEnabled = match.IsEnabled
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new HitTestResponse { Success = false, Error = ex.Message });
            }
        }
    }
}

