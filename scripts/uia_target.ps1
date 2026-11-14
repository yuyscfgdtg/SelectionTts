# Deterministic UIA TextPattern target: a WPF TextBox with all text selected.
# Prints the window handle, then runs until the process is terminated.
# Text is taken from $env:SELECTION_TTS_UIA_TEXT.

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$text = $env:SELECTION_TTS_UIA_TEXT
$window = New-Object System.Windows.Window
$window.Title = 'SelectionTTS UIA target'
$window.Width = 720
$window.Height = 280
$window.Left = 200
$window.Top = 200
$window.Topmost = $true

$box = New-Object System.Windows.Controls.TextBox
$box.Text = $text
$box.TextWrapping = 'Wrap'
$box.AcceptsReturn = $true
$box.FontSize = 18
$window.Content = $box

$window.Show()
$box.Focus() | Out-Null
$box.SelectAll()

$helper = New-Object System.Windows.Interop.WindowInteropHelper($window)
Write-Output $helper.Handle

$window.Add_Closed({
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.InvokeShutdown()
})
[System.Windows.Threading.Dispatcher]::Run()
