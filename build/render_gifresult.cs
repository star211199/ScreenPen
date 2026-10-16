// Renders the GIF result window to a PNG so the layout can be eyeballed.
//
// This window is the answer to "不知道生成的动图在哪里": it shows the file name,
// the frame/duration/size summary, and the full path in a box that can be
// selected and copied. Geometry assertions cannot see whether the path is
// actually legible or whether the buttons collide with it, so capture it.
//
// Usage: build\run_render.ps1 render_gifresult
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using ScreenPen;

static class RenderGifResult
{
    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string outDir = Path.Combine("build", "shots", "render");
        Directory.CreateDirectory(outDir);

        // A file that really exists, so the size line is real rather than a
        // placeholder - a bogus "0 B" would hide a broken FormatSize.
        string tmpDir = Path.Combine(Path.GetTempPath(), "screenpen_gifresult_render");
        Directory.CreateDirectory(tmpDir);
        string gif = Path.Combine(tmpDir, "ScreenPen_20261016_143012.gif");
        File.WriteAllBytes(gif, new byte[412 * 1024]);

        Shot(gif, 62, 11, Path.Combine(outDir, "40_gif_result.png"));

        // A deliberately deep path, which is what makes the path box scroll.
        string deep = Path.Combine(tmpDir,
            "some", "very", "deeply", "nested", "export", "folder", "tree",
            "ScreenPen_20261016_143012.gif");
        Directory.CreateDirectory(Path.GetDirectoryName(deep));
        File.WriteAllBytes(deep, new byte[980]);
        Shot(deep, 3, 1, Path.Combine(outDir, "41_gif_result_longpath.png"));

        try { Directory.Delete(tmpDir, true); }
        catch { }

        Console.WriteLine();
        Console.WriteLine("wrote to " + Path.GetFullPath(outDir));
        return 0;
    }

    static void Shot(string path, int frames, int seconds, string outFile)
    {
        using (GifResultForm f = GifResultForm.CreateForTest(path, frames, seconds))
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-6000, -6000);
            f.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(120);
            Application.DoEvents();

            Rectangle r = f.Bounds;
            using (Bitmap bmp = new Bitmap(r.Width, r.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, r.Width, r.Height));
                bmp.Save(outFile, ImageFormat.Png);
                Console.WriteLine("  wrote " + Path.GetFileName(outFile)
                    + "  " + r.Width + "x" + r.Height
                    + "  path=" + f.PathForTest
                    + "  info=" + f.InfoForTest);
            }

            f.Close();
        }
    }
}
