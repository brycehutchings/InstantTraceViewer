using Hexa.NET.ImGui;
using HexaGen.Runtime;
using System;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace InstantTraceViewerUI
{
    internal static class ImGuiWidgets
    {
        /// <summary>
        /// Runs a slow operation on a background thread while showing a blocking "processing" modal dialog.
        /// Call <see cref="Start"/> to begin an operation and <see cref="Draw"/> every frame to render the
        /// modal until the operation completes. The worker may optionally report progress and observe the
        /// cancellation token (a Cancel button is always shown).
        /// </summary>
        public sealed class ProcessingModal
        {
            // Passed to the worker so it can optionally report a 0..1 fraction and a status message.
            public sealed class Progress
            {
                private readonly ProcessingModal _owner;

                internal Progress(ProcessingModal owner) => _owner = owner;

                public void Report(float fraction, string? status = null)
                {
                    lock (_owner._progressLock)
                    {
                        _owner._fraction = fraction;
                        if (status != null)
                        {
                            _owner._status = status;
                        }
                    }
                }
            }

            // Everything after "###" is the popup identity; the text before it is the visible title.
            private readonly string _popupId = $"###ProcessingModal_{Guid.NewGuid():N}";
            private readonly object _progressLock = new();

            private Task? _task;
            private CancellationTokenSource? _cts;
            private bool _doOpen;
            private string _title = string.Empty;
            private float _fraction = -1f; // Negative means indeterminate (animated bar).
            private string? _status;

            public bool IsRunning => _task is { IsCompleted: false };

            public void Start(string title, Action<Progress, CancellationToken> work)
            {
                if (IsRunning)
                {
                    return; // Ignore a new request while one is already running.
                }

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _title = title;
                _fraction = -1f;
                _status = null;
                _doOpen = true;

                CancellationToken token = _cts.Token;
                Progress progress = new(this);
                _task = Task.Run(() => work(progress, token));
            }

            // Call once per frame. Renders the modal while the operation runs and auto-closes it on completion.
            public void Draw(IUiCommands uiCommands)
            {
                if (_task == null)
                {
                    return;
                }

                if (_doOpen)
                {
                    ImGui.OpenPopup(_popupId);
                    _doOpen = false;
                }

                if (ImGui.BeginPopupModal($"{_title}{_popupId}", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
                {
                    float fraction;
                    string? status;
                    lock (_progressLock)
                    {
                        fraction = _fraction;
                        status = _status;
                    }

                    ImGui.TextUnformatted(status ?? _title);

                    Vector2 barSize = new(ImGui.GetFontSize() * 20, 0);
                    if (fraction < 0)
                    {
                        // Indeterminate: a time-based negative value animates the bar.
                        ImGui.ProgressBar(-1.0f * (float)ImGui.GetTime(), barSize, "");
                    }
                    else
                    {
                        ImGui.ProgressBar(fraction, barSize);
                    }

                    ImGui.BeginDisabled(_cts!.IsCancellationRequested);
                    if (ImGui.Button("Cancel"))
                    {
                        _cts.Cancel();
                    }
                    ImGui.EndDisabled();

                    if (_task.IsCompleted)
                    {
                        ImGui.CloseCurrentPopup();

                        Exception? error = _task.IsFaulted ? (_task.Exception?.InnerException ?? _task.Exception) : null;

                        _cts.Dispose();
                        _cts = null;
                        _task = null;

                        if (error != null)
                        {
                            uiCommands.ShowMessageBox(error.Message, _title, isError: true);
                        }
                    }

                    ImGui.EndPopup();
                }
            }
        }

        public struct CurrentInputTextState
        {
            public uint Id;
            public float ScrollX;
        }

        /// <summary>
        /// Reads ImGui internals not exposed by the public ImGui API.
        /// </summary>
        public static unsafe CurrentInputTextState GetCurrentInputTextState()
        {
            ImGuiContextPtr ctx = ImGui.GetCurrentContext();
            if (ctx.Handle == null)
            {
                return default;
            }

            ImGuiInputTextStatePtr textState = new(&ctx.Handle->InputTextState);
            return new CurrentInputTextState
            {
                Id = textState.ID,
                ScrollX = textState.Scroll.X,
            };
        }

        public static nuint GetInputTextBufferSize(string text, int minimumSize)
        {
            return (nuint)Math.Max(minimumSize, Utils.GetByteCountUTF8(text) + 1);
        }

        // It seems the only way to test hover/active state with non-internal API is to
        // call IsItemActive/IsItemHovered and store the result for the next frame.
        private static uint _lastActiveItem = uint.MaxValue;
        private static int _lastActiveItemFrame = 0;
        private static uint _lastHoveredItem = uint.MaxValue;
        private static int _lastHoveredItemFrame = 0;

        public static void ColorSquare(uint color, string? tooltip = null, int verticalOffset = 0, float widthMultiplier = 1)
        {
            float sz = ImGui.GetTextLineHeight();
            Vector2 p = ImGui.GetCursorScreenPos() + new Vector2(0, verticalOffset);
            ImGui.GetWindowDrawList().AddRectFilled(p, new Vector2(p.X + sz * widthMultiplier, p.Y + sz + verticalOffset), color);
            ImGui.Dummy(new Vector2(sz * widthMultiplier, sz));

            if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            {
                ImGui.SetTooltip(tooltip);
            }
        }

        // Renders a button without any background or border. Text color changes instead.
        public static bool UndecoratedButton(string text, string? tooltip = null)
        {
            uint buttonId = ImGui.GetID(text);

            bool isActive = buttonId == _lastActiveItem && _lastActiveItemFrame == ImGui.GetFrameCount() - 1;
            bool isHovered = buttonId == _lastHoveredItem && _lastHoveredItemFrame == ImGui.GetFrameCount() - 1;
            ImGui.PushStyleColor(ImGuiCol.Text,
                isActive ? ImGui.GetColorU32(ImGuiCol.ButtonActive) :
                isHovered ? ImGui.GetColorU32(ImGuiCol.ButtonHovered) :
                            ImGui.GetColorU32(ImGuiCol.Text));
            ImGui.PushStyleColor(ImGuiCol.Button, 0x00000000);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0x00000000);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0x00000000);

            // Remove padding from button
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));

            bool ret = ImGui.SmallButton(text);

            ImGui.PopStyleVar();

            ImGui.PopStyleColor(4);

            if (ImGui.IsItemActive())
            {
                _lastActiveItem = ImGui.GetItemID();
                _lastActiveItemFrame = ImGui.GetFrameCount();
            }
            else if (ImGui.IsItemHovered())
            {
                _lastHoveredItem = ImGui.GetItemID();
                _lastHoveredItemFrame = ImGui.GetFrameCount();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            {
                ImGui.SetTooltip(tooltip);
            }

            return ret;
        }

        public static void HelpIconToolip(string helpText)
        {
            ImGui.PushID(helpText);

            // Text has an ID of 0 so we use the ID on the ID stack.
            var helpId = ImGui.GetItemID();
            bool isHovered = helpId == _lastHoveredItem && _lastHoveredItemFrame == ImGui.GetFrameCount() - 1;

            ImGui.PushStyleColor(ImGuiCol.Text, isHovered ? ImGui.GetColorU32(ImGuiCol.ButtonHovered) : ImGui.GetColorU32(ImGuiCol.Text));
            ImGui.TextUnformatted("\uF059");
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            {
                ImGui.SetTooltip(helpText);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNone))
            {
                _lastHoveredItem = helpId;
                _lastHoveredItemFrame = ImGui.GetFrameCount();
            }

            ImGui.PopID();
        }

        public static void AddHighlightRowBgColorMenuItems(Action<HighlightRowBgColor> selectionAction)
        {
            float sz = ImGui.GetTextLineHeight();
            foreach (HighlightRowBgColor color in Enum.GetValues<HighlightRowBgColor>())
            {
                // Skip if color value has Obsolete attribute
                if (typeof(HighlightRowBgColor).GetField(color.ToString())!.GetCustomAttribute<ObsoleteAttribute>() != null)
                {
                    continue;
                }

                ImGui.PushID((int)color);

                Vector2 p = ImGui.GetCursorScreenPos();

                if (ImGui.MenuItem(""))
                {
                    selectionAction(color);
                }

                uint colorU32 = AppTheme.GetHighlightRowBgColorU32(color);
                string colorName = AppTheme.GetHighlightRowBgColorName(color);

                ImGui.SetCursorScreenPos(p);
                ColorSquare(colorU32, tooltip: colorName);
                ImGui.SameLine();

                ImGui.TextUnformatted(colorName);

                ImGui.PopID();
            }
        }
    }
}
