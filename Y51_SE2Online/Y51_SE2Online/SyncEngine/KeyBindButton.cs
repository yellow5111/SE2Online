using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keen.VRage.Core.Input;
using Keen.VRage.Input;
using KeyBinding = Y51_SE2Online.Core.KeyBinding;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Shows a hotkey; click it and press a key (with modifiers) to rebind, Esc cancels.</summary>
internal sealed class KeyBindButton : Button
{
    private string _binding = "";

    // SE2's button styles select the exact Button type; without this the subclass gets no template and draws nothing.
    protected override System.Type StyleKeyOverride => typeof(Button);
    private bool _listening;
    internal static bool Listening { get; private set; }
    private HashSet<InputId> _held;
    private KeyBinding? _pressed;
    private readonly HashSet<InputId> _active = new();

    public string Binding
    {
        get => _binding;
        set { _binding = value ?? ""; Content = _binding; }
    }

    public KeyBindButton()
    {
        FontSize = 24; MinHeight = 48; MinWidth = 220;
        Click += (_, _) => Listen();
        // The game raises Avalonia key events only for navigation keys, so the key itself is read from the
        // game's keyboard device below; this just keeps Enter/Space/Esc from reaching the button meanwhile.
        AddHandler(KeyDownEvent, (_, e) => e.Handled |= _listening, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => Cancel();
        DetachedFromVisualTree += (_, _) => Cancel();
    }

    private void Listen()
    {
        if (_listening) return;
        _listening = Listening = true; _held = null; _pressed = null;
        Content = "Press a key...";
        InputDeviceManager.Instance.OnBeforeProcessInput += OnInput;
    }

    private void Stop(string content)
    {
        _listening = Listening = false; Content = content;
        InputDeviceManager.Instance.OnBeforeProcessInput -= OnInput;
    }

    private void Cancel()
    {
        if (_listening) Stop(_binding);
    }

    // Like the game's own rebind dialog: take the key on release, so its press can't also act on the UI.
    private void OnInput(InputDeviceManager devices)
    {
        if (!_listening) return;
        _active.Clear();
        foreach (var keyboard in devices.GetAllDevices(GenericDeviceClass.Keyboard)) keyboard.FillActive(_active);
        if (_held == null) { _held = new HashSet<InputId>(_active); return; } // e.g. the Enter that clicked the button
        _held.IntersectWith(_active);
        if (_pressed is KeyBinding key)
        {
            if (_active.Contains(new DigitalInput(key.Vk, GenericDeviceClass.Keyboard))) return;
            _pressed = null;
            if (key.Vk == KeyboardInputs.Escape.Id.Index) { Cancel(); return; }
            string text = KeyBinding.Format(key.Vk, key.Ctrl, key.Shift, key.Alt);
            if (text == null) { Content = "Unsupported key, try another"; return; }
            _binding = text;
            Stop(text);
            return;
        }
        foreach (var input in _active)
        {
            if (_held.Contains(input) || input == KeyboardInputs.Control.Id || input == KeyboardInputs.Shift.Id || input == KeyboardInputs.Alt.Id
                || input == KeyboardInputs.LWin.Id || input == KeyboardInputs.RWin.Id) continue;
            _pressed = new KeyBinding(input.Index, _active.Contains(KeyboardInputs.Control), _active.Contains(KeyboardInputs.Shift), _active.Contains(KeyboardInputs.Alt));
            return;
        }
    }
}
