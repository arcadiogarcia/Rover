using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Threading.Tasks;
using zRover.Core;
using zRover.Core.Tools.InputInjection;
using Windows.UI.Input.Preview.Injection;

namespace zRover.Uwp.Capabilities
{
    public sealed partial class InputInjectionCapability
    {
        private void RegisterKeyboardTools(IMcpToolRegistry registry)
        {
            registry.RegisterTool(
                "inject_key_press",
                "Injects a keyboard key press with optional modifier keys. " +
                "Supports all Windows virtual key names. " +
                "Use holdDurationMs for long-press scenarios (e.g. holding a key in a game).",
                ToolSchemas.KeyPressSchema,
                InjectKeyPressAsync);

            registry.RegisterTool(
                "inject_text",
                "Types a string of text by injecting individual key presses for each character. " +
                "Handles uppercase letters and common symbols by automatically applying Shift. " +
                "For special keys (Enter, Tab, etc.), use inject_key_press instead.",
                ToolSchemas.TextSchema,
                InjectTextAsync);
        }

        private async Task<string> InjectKeyPressAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectKeyPressRequest>(argsJson)
                      ?? new InjectKeyPressRequest();

            LogToFile($"InjectKeyPressAsync: key={req.Key} modifiers=[{string.Join(",", req.Modifiers)}] hold={req.HoldDurationMs}ms");

            var injector = _injector;
            if (injector == null || _runOnUiThread == null)
            {
                return InjectorUnavailableResponse(new InjectKeyPressResponse
                {
                    Success = false,
                    Key = req.Key,
                    Modifiers = req.Modifiers
                });
            }

            Exception? error = null;
            await _runOnUiThread(() => { try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { } return Task.CompletedTask; }).ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);
            await _runOnUiThread(() =>
            {
                try
                {
                    var vk = ParseVirtualKey(req.Key);

                    // Press modifiers
                    foreach (var mod in req.Modifiers)
                    {
                        var modVk = ParseVirtualKey(mod);
                        injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                        {
                            VirtualKey = (ushort)modVk,
                            KeyOptions = InjectedInputKeyOptions.None
                        }});
                    }

                    // Press key
                    injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                    {
                        VirtualKey = (ushort)vk,
                        KeyOptions = InjectedInputKeyOptions.None
                    }});

                    if (req.HoldDurationMs > 0)
                        System.Threading.Thread.Sleep(req.HoldDurationMs);

                    // Release key
                    injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                    {
                        VirtualKey = (ushort)vk,
                        KeyOptions = InjectedInputKeyOptions.KeyUp
                    }});

                    // Release modifiers in reverse order
                    for (int i = req.Modifiers.Count - 1; i >= 0; i--)
                    {
                        var modVk = ParseVirtualKey(req.Modifiers[i]);
                        injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
                        {
                            VirtualKey = (ushort)modVk,
                            KeyOptions = InjectedInputKeyOptions.KeyUp
                        }});
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (error != null)
            {
                LogToFile($"InjectKeyPress FAILED: {error.Message}");
                return JsonConvert.SerializeObject(new InjectKeyPressResponse
                {
                    Success = false,
                    Key = req.Key,
                    Modifiers = req.Modifiers
                });
            }

            LogToFile("InjectKeyPress succeeded");
            return JsonConvert.SerializeObject(new InjectKeyPressResponse
            {
                Success = true,
                Key = req.Key,
                Modifiers = req.Modifiers
            });
        }

        private async Task<string> InjectTextAsync(string argsJson)
        {
            var req = JsonConvert.DeserializeObject<InjectTextRequest>(argsJson)
                      ?? new InjectTextRequest();

            LogToFile($"InjectTextAsync: length={req.Text?.Length ?? 0} delay={req.DelayBetweenKeysMs}ms");

            var injector = _injector;
            if (injector == null || _runOnUiThread == null || string.IsNullOrEmpty(req.Text))
            {
                return InjectorUnavailableResponse(new InjectTextResponse
                {
                    Success = false,
                    CharacterCount = 0
                });
            }

            await _runOnUiThread(() => { try { Windows.UI.Xaml.Window.Current?.Activate(); } catch { } return Task.CompletedTask; }).ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);

            int typed = 0;
            Exception? error = null;

            foreach (char c in req.Text!)
            {
                if (typed > 0 && req.DelayBetweenKeysMs > 0)
                    await Task.Delay(req.DelayBetweenKeysMs).ConfigureAwait(false);

                char ch = c;
                await _runOnUiThread(() =>
                {
                    try
                    {
                        InjectCharacter(injector, ch);
                        typed++;
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                if (error != null) break;
            }

            if (error != null)
                LogToFile($"InjectText FAILED at char {typed}: {error.Message}");
            else
                LogToFile($"InjectText succeeded: {typed} chars");

            return JsonConvert.SerializeObject(new InjectTextResponse
            {
                Success = error == null,
                CharacterCount = typed
            });
        }

        private void InjectCharacter(InputInjector injector, char c)
        {
            // Use Unicode scan code injection for broad character support
            injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
            {
                ScanCode = (ushort)c,
                KeyOptions = InjectedInputKeyOptions.Unicode
            }});
            injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo
            {
                ScanCode = (ushort)c,
                KeyOptions = InjectedInputKeyOptions.Unicode | InjectedInputKeyOptions.KeyUp
            }});
        }

        private static ushort ParseVirtualKey(string keyName) => VirtualKeyHelper.ParseVirtualKey(keyName);
    }
}
