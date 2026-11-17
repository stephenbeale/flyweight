namespace Flyweight.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        LibVLCSharp.Shared.Core.Initialize(); // loads the native libvlc shipped by VideoLAN.LibVLC.Windows

        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "Flyweight: unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm());
    }
}
