using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using ScreenPen;

// Verifies the 贴图 (pin-to-desktop) feature end to end:
//
//   * the region selector offers a 4th 贴图 button, its hit-test returns
//     PinToDesktop, and a bare P does the same;
//   * the old 3-argument constructor still behaves exactly as before;
//   * PinForm's geometry maths is recompute-from-base, so repeated zooming
//     never accumulates rounding drift (that is why it avoids Form.Scale);
//   * the bitmap handed to PinForm is released exactly once - never leaked and
//     never double-freed - including when the constructor is fed a null image;
//   * AppContext2.PinScreenshot tracks the window so quitting cannot orphan it.
static class PinProbe
{
    static int fails;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what +
                          (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }

    static void Pump(int ms)
    {
        for (int i = 0; i < Math.Max(1, ms / 25); i++)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }
    }

    static object Field(object o, string name)
    {
        FieldInfo fi = o.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (fi == null) throw new Exception("no field " + name);
        return fi.GetValue(o);
    }

    /// <summary>
    /// Invokes by name and *argument count*. The count matters: Dispose() and
    /// Dispose(bool) both exist on Form, so a plain GetMethod(name) throws
    /// AmbiguousMatchException.
    /// </summary>
    static void Call(object o, string name, params object[] args)
    {
        if (args == null) args = new object[0];
        MethodInfo found = null;
        foreach (MethodInfo mi in o.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (mi.Name != name) continue;
            if (mi.GetParameters().Length != args.Length) continue;
            found = mi;
            break;
        }
        if (found == null)
            throw new Exception("no method " + name + " taking " + args.Length + " argument(s)");
        found.Invoke(o, args);
    }

    /// <summary>True when accessing the bitmap throws, i.e. it has been freed.</summary>
    static bool IsFreed(Bitmap b)
    {
        if (b == null) return true;
        try { int w = b.Width; GC.KeepAlive(w); return false; }
        catch (Exception) { return true; }
    }

    static Bitmap MakeImage(int w, int h, Color c)
    {
        Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b)) g.Clear(c);
        return b;
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Settings cfg = new Settings();

        const int W = 800, H = 600;
        Rectangle desktop = new Rectangle(0, 0, W, H);

        // ==================================================================
        // 1. Region selector: the 贴图 button and its wiring.
        // ==================================================================
        Console.WriteLine("--- region selector: pin wiring ---");

        RegionSelectForm f = new RegionSelectForm(cfg, desktop, MakeImage(W, H, Color.Teal),
                                                 RegionAction.PinToDesktop);
        f.Show();
        Pump(300);

        Check(f.DefaultActionForTest == RegionAction.PinToDesktop,
              "4-arg ctor records PinToDesktop as the default action",
              f.DefaultActionForTest.ToString());

        f.SimulateDrag(new Point(120, 90), new Point(460, 350));
        Pump(120);

        Rectangle bs = f.SaveButtonBounds, bc = f.CopyButtonBounds;
        Rectangle bp = f.PinButtonBounds, bx = f.CancelButtonBounds;
        Console.WriteLine("  save=" + bs + " copy=" + bc + " pin=" + bp + " cancel=" + bx);

        Check(bp.Width > 0 && bp.Height > 0, "the 贴图 button has a real size", bp.ToString());

        Rectangle client = new Rectangle(0, 0, f.ClientSize.Width, f.ClientSize.Height);
        Check(client.Contains(bs) && client.Contains(bc) &&
              client.Contains(bp) && client.Contains(bx),
              "all four toolbar buttons fit inside the window", "");

        bool overlap =
            bp.IntersectsWith(bs) || bp.IntersectsWith(bc) || bp.IntersectsWith(bx) ||
            bs.IntersectsWith(bc) || bs.IntersectsWith(bx) || bc.IntersectsWith(bx);
        Check(!overlap, "the four buttons never overlap each other", "");

        Rectangle toolbar = f.ToolbarBoundsForTest;
        Check(client.Contains(toolbar),
              "the whole toolbar stays on screen",
              toolbar + " in " + client);

        // The toolbar must not be wide enough to run off a narrow desktop: the
        // layout shrinks the buttons instead of letting them clip.
        Check(toolbar.Width <= client.Width,
              "toolbar width never exceeds the window width",
              toolbar.Width + " vs " + client.Width);

        // Committing PinToDesktop must still yield the cropped pixels.
        f.SimulateFinish(RegionAction.PinToDesktop);
        Pump(150);
        Check(f.ActionTaken == RegionAction.PinToDesktop,
              "ActionTaken is PinToDesktop after choosing 贴图",
              f.ActionTaken.ToString());
        Bitmap pinCrop = f.CropSelection();
        Check(pinCrop != null, "the pin action still produces cropped pixels",
              pinCrop == null ? "null" : pinCrop.Width + "x" + pinCrop.Height);
        if (pinCrop != null) pinCrop.Dispose();

        // Bare P is the keyboard shortcut for the same action.
        RegionSelectForm fk = new RegionSelectForm(cfg, desktop, MakeImage(W, H, Color.Teal),
                                                  RegionAction.SaveToFile);
        fk.Show();
        Pump(200);
        fk.SimulateDrag(new Point(60, 60), new Point(300, 240));
        Call(fk, "OnKeyDown", new KeyEventArgs(Keys.P));
        Pump(120);
        Check(fk.ActionTaken == RegionAction.PinToDesktop,
              "bare P pins the selection",
              fk.ActionTaken.ToString());

        // Back-compat: the original 3-argument constructor.
        RegionSelectForm f3 = new RegionSelectForm(cfg, desktop, MakeImage(W, H, Color.Teal));
        f3.Show();
        Pump(200);
        Check(f3.DefaultActionForTest == RegionAction.SaveToFile,
              "the old 3-arg ctor still defaults to SaveToFile",
              f3.DefaultActionForTest.ToString());
        f3.SimulateDrag(new Point(80, 80), new Point(320, 260));
        Call(f3, "OnKeyDown", new KeyEventArgs(Keys.Enter));
        Pump(120);
        Check(f3.ActionTaken == RegionAction.SaveToFile,
              "Enter still commits the old default action",
              f3.ActionTaken.ToString());

        // Cancelled must be normalised, never silently made into a real action.
        RegionSelectForm f4 = new RegionSelectForm(cfg, desktop, MakeImage(W, H, Color.Teal),
                                                  RegionAction.Cancelled);
        f4.Show();
        Pump(200);
        Check(f4.DefaultActionForTest == RegionAction.SaveToFile,
              "a Cancelled default is normalised to SaveToFile",
              f4.DefaultActionForTest.ToString());

        try { f.Close(); } catch { }
        try { fk.Close(); } catch { }
        try { f3.Close(); } catch { }
        try { f4.Close(); } catch { }
        Pump(150);

        // ==================================================================
        // 2. PinForm geometry: scaling is recompute-from-base, not Form.Scale.
        // ==================================================================
        Console.WriteLine();
        Console.WriteLine("--- PinForm geometry ---");

        Bitmap img = MakeImage(200, 120, Color.Crimson);
        PinForm pin = new PinForm(img, new Rectangle(300, 300, 200, 120));
        pin.Show();
        Pump(300);

        Check(pin.TestBaseSize.Width == 200 && pin.TestBaseSize.Height == 120,
              "the base size is the image size",
              pin.TestBaseSize.ToString());
        Check(pin.ClientSize.Width == 200 && pin.ClientSize.Height == 120,
              "at scale 1.0 the client area is the image size",
              pin.ClientSize.ToString());
        Check(Math.Abs(pin.TestScale - 1.0f) < 0.001f,
              "the initial scale is 1.0", pin.TestScale.ToString());
        Check(!pin.IsDisposed, "the pin window is alive", "");
        Check(!pin.TestIsClosed, "a fresh pin is not closed", "");

        // Zoom in doubles the window exactly.
        pin.TestSetScale(2.0f);
        Pump(80);
        Check(pin.ClientSize.Width == 400 && pin.ClientSize.Height == 240,
              "scale 2.0 doubles the client area exactly",
              pin.ClientSize.ToString());

        // Limits are clamped, not wrapped or ignored.
        pin.TestSetScale(0.0001f);
        Pump(80);
        Check(Math.Abs(pin.TestScale - PinForm.MinScale) < 0.001f,
              "zoom below the minimum clamps to MinScale",
              pin.TestScale.ToString());
        Check(pin.ClientSize.Width >= 1 && pin.ClientSize.Height >= 1,
              "an extreme zoom never produces a zero-size window",
              pin.ClientSize.ToString());

        pin.TestSetScale(9999f);
        Pump(80);
        Check(Math.Abs(pin.TestScale - PinForm.MaxScale) < 0.001f,
              "zoom above the maximum clamps to MaxScale",
              pin.TestScale.ToString());

        // The drift test: many zooms, then back to 1.0, must land exactly on the
        // base size. Form.Scale would have accumulated rounding error here.
        for (int i = 0; i < 40; i++)
        {
            pin.TestSetScale(1.0f + (i % 7) * 0.13f);
            pin.TestSetScale(1.0f);
        }
        Pump(80);
        Check(pin.TestScale == 1.0f ||
              Math.Abs(pin.TestScale - 1.0f) < 0.0005f,
              "returning to scale 1.0 restores the exact scale",
              pin.TestScale.ToString());
        Check(pin.ClientSize.Width == 200 && pin.ClientSize.Height == 120,
              "40 zoom round-trips leave no size drift",
              pin.ClientSize.ToString());

        // Wheel zoom multiplies by 1.12 per notch.
        pin.TestWheelZoom(120, new Point(100, 60));
        Pump(80);
        Check(Math.Abs(pin.TestScale - 1.12f) < 0.002f,
              "one wheel notch zooms by 1.12x", pin.TestScale.ToString());
        pin.TestWheelZoom(-120, new Point(100, 60));
        Pump(80);
        Check(Math.Abs(pin.TestScale - 1.0f) < 0.005f,
              "a wheel notch back returns to the previous scale",
              pin.TestScale.ToString());

        // The cursor-anchored zoom must keep the point under the cursor put.
        pin.TestSetScale(1.0f);
        Pump(50);
        Point anchor = new Point(150, 90);
        int screenAnchorX = pin.Left + anchor.X;
        int screenAnchorY = pin.Top + anchor.Y;
        pin.TestWheelZoom(240, anchor);       // two notches
        Pump(80);
        Check(Math.Abs((pin.Left + (int)Math.Round(anchor.X * pin.TestScale)) - screenAnchorX) <= 2 &&
              Math.Abs((pin.Top + (int)Math.Round(anchor.Y * pin.TestScale)) - screenAnchorY) <= 2,
              "the point under the cursor stays put while zooming",
              "drift=(" + ((pin.Left + (int)Math.Round(anchor.X * pin.TestScale)) - screenAnchorX) +
              "," + ((pin.Top + (int)Math.Round(anchor.Y * pin.TestScale)) - screenAnchorY) + ")");

        // Dragging moves the window by exactly the delta.
        pin.TestSetScale(1.0f);
        Pump(50);
        Point before = pin.Location;
        pin.TestDragBy(40, -25);
        Pump(50);
        Check(pin.Location.X == before.X + 40 && pin.Location.Y == before.Y - 25,
              "dragging moves the window by exactly the delta",
              before + " -> " + pin.Location);

        // A zero drag must not nudge the window.
        Point held = pin.Location;
        pin.TestDragBy(0, 0);
        Check(pin.Location == held, "a zero-distance drag does not move the window",
              pin.Location.ToString());

        // The close button lives inside the window and shrinks with it.
        Rectangle cb = pin.CloseButtonBounds;
        Check(cb.Width > 0 && cb.Height > 0, "the close button has a real size", cb.ToString());
        Check(new Rectangle(0, 0, pin.ClientSize.Width, pin.ClientSize.Height).Contains(cb),
              "the close button is inside the client area", cb.ToString());

        pin.TestSetScale(PinForm.MinScale);
        Pump(80);
        Rectangle cbSmall = pin.CloseButtonBounds;
        Check(cbSmall.Width <= 0 || cbSmall.Width <= pin.ClientSize.Width,
              "the close button never exceeds the shrunken window",
              cbSmall + " in " + pin.ClientSize);
        pin.TestSetScale(1.0f);
        Pump(50);

        // Esc must close the window.
        Call(pin, "OnKeyDown", new KeyEventArgs(Keys.Escape));
        Pump(300);
        Check(pin.TestIsClosed, "Esc closes the pin window", "closed=" + pin.TestIsClosed);
        Check(IsFreed(img), "closing the window releases the owned bitmap too", "");

        // ==================================================================
        // 3. Bitmap ownership: exactly one free, never a leak, never a double.
        // ==================================================================
        Console.WriteLine();
        Console.WriteLine("--- bitmap ownership ---");

        // A pin that is explicitly disposed (rather than closed by the user)
        // must still release the bitmap exactly once.
        Bitmap own = MakeImage(150, 100, Color.DarkOrange);
        PinForm ownPin = new PinForm(own, new Rectangle(200, 200, 150, 100));
        ownPin.Show();
        Pump(200);
        Check(!IsFreed(own), "the bitmap is alive while the pin is open", "");

        Call(ownPin, "Dispose");
        Pump(150);
        Check(ownPin.TestImage == null,
              "Dispose clears the owned bitmap reference", "");
        Check(IsFreed(own), "Dispose releases the caller's bitmap", "");

        // Repeated Dispose must not throw and must not free anything twice.
        bool doubleOk = true;
        try { ownPin.Dispose(); ownPin.Dispose(); }
        catch (Exception ex) { doubleOk = false; Console.WriteLine("      " + ex.GetType().Name + ": " + ex.Message); }
        Check(doubleOk, "Dispose is idempotent", "");

        // A null image must not throw out of the constructor.
        bool nullOk = true;
        PinForm nullPin = null;
        try { nullPin = new PinForm(null, new Rectangle(100, 100, 240, 160)); }
        catch (Exception ex) { nullOk = false; Console.WriteLine("      " + ex.GetType().Name + ": " + ex.Message); }
        Check(nullOk, "a null image does not throw out of the constructor", "");
        if (nullPin != null)
        {
            nullPin.Show();
            Pump(150);
            Check(nullPin.ClientSize.Width > 0 && nullPin.ClientSize.Height > 0,
                  "a null-image pin still gets a usable size",
                  nullPin.ClientSize.ToString());
            bool nullDisposeOk = true;
            try { nullPin.Close(); nullPin.Dispose(); }
            catch (Exception ex) { nullDisposeOk = false; Console.WriteLine("      " + ex.Message); }
            Check(nullDisposeOk, "a null-image pin disposes cleanly", "");
        }

        // ==================================================================
        // 4. Several pins are independent.
        // ==================================================================
        Console.WriteLine();
        Console.WriteLine("--- independent pins ---");

        Bitmap imgA = MakeImage(160, 100, Color.Gold);
        Bitmap imgB = MakeImage(160, 100, Color.SeaGreen);
        PinForm pinA = new PinForm(imgA, new Rectangle(120, 500, 160, 100));
        PinForm pinB = new PinForm(imgB, new Rectangle(400, 500, 160, 100));
        pinA.Show(); pinB.Show();
        Pump(250);

        Check(!ReferenceEquals(pinA.TestImage, pinB.TestImage),
              "each pin holds its own bitmap", "");
        pinA.TestSetScale(2.0f);
        Pump(80);
        Check(pinA.ClientSize.Width == 320 && pinB.ClientSize.Width == 160,
              "zooming one pin leaves the other untouched",
              pinA.ClientSize + " / " + pinB.ClientSize);

        pinA.Close(); pinA.Dispose();
        Pump(150);
        Check(IsFreed(imgA), "closing a pin frees its own bitmap", "");
        Check(!IsFreed(imgB), "closing one pin does not free another's bitmap", "");
        Check(pinB.ClientSize.Width == 160, "the surviving pin is unaffected",
              pinB.ClientSize.ToString());
        pinB.Close(); pinB.Dispose();
        Pump(100);
        Check(IsFreed(imgB), "the second pin frees its bitmap on close", "");

        // ==================================================================
        // 5. Integration: the controller tracks and closes every pin, so
        //    quitting cannot leave orphaned windows on the desktop.
        // ==================================================================
        Console.WriteLine();
        Console.WriteLine("--- controller integration ---");

        AppContext2 ctx = new AppContext2(true);
        List<PinForm> pins = (List<PinForm>)Field(ctx, "_pins");
        Check(pins != null && pins.Count == 0, "a fresh controller has no pins",
              pins == null ? "null" : pins.Count.ToString());

        Bitmap handed = MakeImage(180, 120, Color.MediumPurple);
        Call(ctx, "PinScreenshot", handed, new Rectangle(500, 400, 180, 120));
        Pump(350);

        Check(pins.Count == 1, "PinScreenshot tracks the new window",
              "pins=" + pins.Count);
        Check(!IsFreed(handed), "the handed-over bitmap is still alive while pinned", "");
        if (pins.Count == 1)
        {
            Check(pins[0].TestImage != null, "the pin owns the bitmap", "");
            Check(pins[0].TopMost, "the pin is always on top", "");
            Check(pins[0].ClientSize.Width == 180 && pins[0].ClientSize.Height == 120,
                  "the pin is sized to the captured region",
                  pins[0].ClientSize.ToString());
        }

        // A second pin must be tracked alongside the first.
        Bitmap handed2 = MakeImage(140, 90, Color.SaddleBrown);
        Call(ctx, "PinScreenshot", handed2, new Rectangle(700, 400, 140, 90));
        Pump(350);
        Check(pins.Count == 2, "a second PinScreenshot is tracked too",
              "pins=" + pins.Count);

        // Quitting must close them all - and free both bitmaps exactly once.
        Call(ctx, "CloseAllPins");
        Pump(250);
        Check(pins.Count == 0, "CloseAllPins clears the tracking list",
              "pins=" + pins.Count);
        Check(IsFreed(handed), "CloseAllPins frees the first pin's bitmap", "");
        Check(IsFreed(handed2), "CloseAllPins frees the second pin's bitmap", "");

        bool closeAgainOk = true;
        try { Call(ctx, "CloseAllPins"); }
        catch (Exception ex) { closeAgainOk = false; Console.WriteLine("      " + ex.Message); }
        Check(closeAgainOk, "closing every pin twice is safe", "");

        try { ((IDisposable)ctx).Dispose(); } catch { }
        Pump(150);

        Console.WriteLine();
        if (fails == 0) { Console.WriteLine("ALL PIN CHECKS PASSED"); return; }
        Console.WriteLine(fails + " PIN CHECK(S) FAILED");
        Environment.Exit(1);
    }
}
