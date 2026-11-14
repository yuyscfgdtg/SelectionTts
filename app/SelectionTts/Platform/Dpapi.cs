using System.Runtime.InteropServices;
using System.Text;
using SelectionTts.Core;

namespace SelectionTts.Platform;

/// <summary>
/// 用 Windows DPAPI（CurrentUser）加密敏感字符串，只有当前用户能解开。
/// 直接 P/Invoke，不引入额外依赖。
/// </summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return "";
        }

        var bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob();
        var output = new DataBlob();

        try
        {
            input.Data = Marshal.AllocHGlobal(bytes.Length);
            input.Size = bytes.Length;
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);

            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new InvalidOperationException($"加密失败，错误码 {Marshal.GetLastWin32Error()}");
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return Convert.ToBase64String(result);
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.Data);
            }

            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return "";
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return "";
        }

        var input = new DataBlob();
        var output = new DataBlob();

        try
        {
            input.Data = Marshal.AllocHGlobal(encrypted.Length);
            input.Size = encrypted.Length;
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                Log.Warn($"解密失败，错误码 {Marshal.GetLastWin32Error()}");
                return "";
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return Encoding.UTF8.GetString(result);
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.Data);
            }

            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
