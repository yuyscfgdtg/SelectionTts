namespace SelectionTts.Platform;

internal static class KeyboardInput
{
    /// <summary>
    /// 只发送 Ctrl+C，不激活任何窗口。
    /// 全局热键回调时修饰键可能仍被按住，先等它们松开，避免目标窗口收到 Ctrl+Alt+C。
    /// </summary>
    public static void SendCtrlC()
    {
        WaitForModifiersReleased(600);
        Thread.Sleep(30);

        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, down: true),
            Key(NativeMethods.VK_C, down: true),
            Key(NativeMethods.VK_C, down: false),
            Key(NativeMethods.VK_CONTROL, down: false),
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void WaitForModifiersReleased(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var held =
                (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

            if (!held)
            {
                return;
            }

            Thread.Sleep(15);
        }
    }

    private static NativeMethods.INPUT Key(int virtualKey, bool down) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KEYBDINPUT
            {
                VirtualKey = (ushort)virtualKey,
                Flags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP,
            },
        },
    };
}
