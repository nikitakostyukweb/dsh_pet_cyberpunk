// Входящий вызов в духе аниме-киберпанка: окно с настоящей прозрачностью, въезжает слева с глитчем.
// Портрет звонящего с голо-помехами и пульсом, заголовок вида (ВХОДЯЩИЙ ВЫЗОВ / ВОПРОС / ПОДТВЕРЖДЕНИЕ),
// имя, текст, ОДНА кнопка «ПРИНЯТЬ»: клик фокусирует окно DeepSeek Harness и закрывает карточку.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class CallForm : Form
{
    // Подписи на английском (настройка языка в меню питомца).
    public bool En;
    const int W = 480, H = 176;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte op, flags, alpha, format; }
    [StructLayout(LayoutKind.Sequential)] struct BMIH { public int size, width, height; public short planes, bits; public int compression, imageSize, xppm, yppm, used, important; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BMIH bi, uint usage, out IntPtr bits, IntPtr sec, uint off);

    readonly IntPtr memDc, dib, oldBmp, bits;
    readonly Bitmap canvas;
    readonly Graphics g;
    readonly Random rnd = new Random();
    static readonly Color RED = Color.FromArgb(255, 59, 71), CYAN = Color.FromArgb(46, 242, 255), YELLOW = Color.FromArgb(252, 238, 10);
    static readonly Color GREEN = Color.FromArgb(57, 255, 136);
    readonly Font fontLabel = new Font("Bahnschrift", 9f, FontStyle.Bold);
    readonly Font fontName = new Font("Bahnschrift", 22f, FontStyle.Bold);
    readonly Font fontText = new Font("Bahnschrift", 11f, FontStyle.Regular);
    readonly Font fontKey = new Font("Bahnschrift", 9.5f, FontStyle.Bold);
    // Типографский формат — без запасных полей шрифта: подпись, имя и текст встают ровно по одной левой линии.
    readonly StringFormat tight = (StringFormat)StringFormat.GenericTypographic.Clone();

    string caller = "KADR", text = "";
    Bitmap portrait;
    // Квадратный кроп головы звонящего: вписывается в квадратную рамку 1:1, поэтому лицо не сплющивается.
    RectangleF portraitSrc;
    // Вид карточки: call — готовность (зелёный), question — вопрос (жёлтый), approval — подтверждение (красный).
    public string Kind = "call";
    Color edge = GREEN, accent = GREEN;
    readonly DateTime shownAt = DateTime.Now;
    RectangleF answerRect;
    public int Left0, Top0;

    public event EventHandler Answered;

    public CallForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        var bi = new BMIH { size = 40, width = W, height = -H, planes = 1, bits = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screen);
        dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDc, dib);
        ReleaseDC(IntPtr.Zero, screen);
        canvas = new Bitmap(W, H, W * 4, PixelFormat.Format32bppPArgb, bits);
        g = Graphics.FromImage(canvas);
        Cursor = Cursors.Hand;
        MouseUp += (s, e) =>
        {
            // Одна кнопка «ПРИНЯТЬ»: клик в любом месте карточки — фокус Harness + закрыть (невидимых зон нет).
            if (Answered != null) Answered(this, EventArgs.Empty);
        };
    }

    // Слоистое окно без кнопки на панели задач.
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80; return cp; }
    }

    public void Setup(string kind, string who, string what, string portraitPath)
    {
        // Вид: call (готовность) — зелёный, question — жёлтый, approval — красный. Без -Kind — call.
        Kind = kind == "question" ? "question" : kind == "approval" ? "approval" : "call";
        if (Kind == "question") { edge = YELLOW; accent = YELLOW; }
        else if (Kind == "approval") { edge = RED; accent = RED; }
        else { edge = GREEN; accent = GREEN; }
        caller = string.IsNullOrEmpty(who) ? "KADR" : who.ToUpperInvariant();
        text = what ?? "";
        if (!string.IsNullOrEmpty(portraitPath) && File.Exists(portraitPath))
            using (var ms = new MemoryStream(File.ReadAllBytes(portraitPath)))
            using (var decoded = new Bitmap(ms))
            {
                portrait = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
                using (var cg = Graphics.FromImage(portrait)) cg.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
                portraitSrc = HeadCrop(portrait);
            }
    }

    // Кроп головы: по непрозрачным пикселям находим фигуру героя и берём верхнюю часть квадратом.
    // Квадратный кроп → квадратная рамка = пропорции сохраняются (UniformToFill без искажений).
    static RectangleF HeadCrop(Bitmap bmp)
    {
        Rectangle bbox = OpaqueBox(bmp);
        if (bbox.Width <= 0 || bbox.Height <= 0) return new RectangleF(0, 0, bmp.Width, bmp.Height);
        float side = Math.Max(bbox.Width * 0.62f, bbox.Height * 0.30f);
        side = Math.Max(40f, Math.Min(side, Math.Min(bmp.Width, bmp.Height)));
        float cx = bbox.Left + bbox.Width / 2f;
        float top = bbox.Top - side * 0.05f;
        float left = cx - side / 2f;
        if (left < 0) left = 0;
        if (top < 0) top = 0;
        if (left + side > bmp.Width) left = bmp.Width - side;
        if (top + side > bmp.Height) top = bmp.Height - side;
        return new RectangleF(left, top, side, side);
    }

    // Непрозрачная часть картинки героя (альфа > 40): без неё кроп головы попал бы в пустое поле PNG.
    static Rectangle OpaqueBox(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            int stride = data.Stride;
            byte[] bytes = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < bmp.Height; y += 2)
                for (int x = 0; x < bmp.Width; x += 2)
                    if (bytes[y * stride + x * 4 + 3] > 40)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return Rectangle.Empty;
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        finally { bmp.UnlockBits(data); }
    }

    static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }

    public void Tick()
    {
        double age = (DateTime.Now - shownAt).TotalMilliseconds;
        double now = Environment.TickCount / 1000.0;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Въезд слева и мерцание включения.
        float ox = (float)(-70 * (1 - Ease(age / 260)));
        bool flickerOff = age < 320 && ((int)(age / 40)) % 3 == 1;
        if (!flickerOff) DrawCard(ox, age, now);
        Present();
    }

    void DrawCard(float ox, double age, double now)
    {
        float x0 = 10 + ox, y0 = 10, w = W - 20, h = H - 20, c = 18;
        var pts = new[]
        {
            new PointF(x0, y0), new PointF(x0 + w - c, y0), new PointF(x0 + w, y0 + c),
            new PointF(x0 + w, y0 + h), new PointF(x0 + c, y0 + h), new PointF(x0, y0 + h - c),
        };
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(pts);
            for (int i = 4; i >= 1; i--)
                using (var p = new Pen(Color.FromArgb(20, edge), i * 3.5f)) g.DrawPath(p, path);
            using (var fill = new LinearGradientBrush(new PointF(x0, y0), new PointF(x0 + w, y0), Color.FromArgb(240, 26, 8, 12), Color.FromArgb(232, 8, 12, 16)))
                g.FillPath(fill, path);
            using (var p = new Pen(edge, 2)) g.DrawPath(p, path);
        }
        // Цветная полоса слева со свечением (цвет зависит от вида: звонок/вопрос/подтверждение).
        for (int i = 3; i >= 1; i--)
            using (var p = new Pen(Color.FromArgb(40, accent), 4 + i * 3)) g.DrawLine(p, x0 + 4, y0 + 16, x0 + 4, y0 + h - 22);
        using (var p = new Pen(accent, 3)) g.DrawLine(p, x0 + 4, y0 + 16, x0 + 4, y0 + h - 22);

        // Портрет: голова героя, помехи, сканлайны, пульсирующее кольцо вызова.
        var box = new RectangleF(x0 + 18, y0 + 18, 100, 100);
        double pulse = (now % 1.1) / 1.1;
        using (var p = new Pen(Color.FromArgb((int)(140 * (1 - pulse)), accent), 2))
            g.DrawRectangle(p, box.X - (float)(pulse * 10), box.Y - (float)(pulse * 10), box.Width + (float)(pulse * 20), box.Height + (float)(pulse * 20));
        using (var b = new SolidBrush(Color.FromArgb(255, 6, 16, 20))) g.FillRectangle(b, box);
        if (portrait != null)
        {
            // Квадратный кроп головы в квадратную рамку: пропорции сохранены (без растяжения/сплющивания).
            g.DrawImage(portrait, box, portraitSrc, GraphicsUnit.Pixel);
            // Голо-помехи: сдвинутые полоски в первые доли секунды и изредка потом.
            if (age < 450 || rnd.NextDouble() < 0.03)
                for (int i = 0; i < 3; i++)
                {
                    float sy = box.Y + rnd.Next(0, (int)box.Height - 8);
                    float shift = rnd.Next(-8, 9);
                    var slice = new RectangleF(portraitSrc.X, portraitSrc.Y + (sy - box.Y) / box.Height * portraitSrc.Height, portraitSrc.Width, portraitSrc.Height * 8 / box.Height);
                    g.DrawImage(portrait, new RectangleF(box.X + shift, sy, box.Width, 8), slice, GraphicsUnit.Pixel);
                }
        }
        using (var b = new SolidBrush(Color.FromArgb(28, accent)))
            for (float sy = box.Y; sy < box.Bottom; sy += 3) g.FillRectangle(b, box.X, sy, box.Width, 1);
        using (var p = new Pen(accent, 1.5f)) g.DrawRectangle(p, box.X, box.Y, box.Width, box.Height);

        // Подписи: заголовок вида с бегущими точками, имя с глитч-тенями, строка о сделанном.
        float tx = box.Right + 18;
        string dots = new string('.', (int)(now * 3) % 4);
        string head = Kind == "question" ? (En ? "QUESTION" : "ВОПРОС")
                    : Kind == "approval" ? (En ? "CONFIRMATION" : "ПОДТВЕРЖДЕНИЕ")
                    : (En ? "INCOMING CALL" : "ВХОДЯЩИЙ ВЫЗОВ");
        string glyph = Kind == "question" ? "?  " : Kind == "approval" ? "!  " : "";
        using (var b = new SolidBrush(accent)) g.DrawString(glyph + head + dots, fontLabel, b, tx, y0 + 18, tight);
        if (age < 500 || rnd.NextDouble() < 0.02)
        {
            using (var b = new SolidBrush(Color.FromArgb(160, edge))) g.DrawString(caller, fontName, b, tx - 2, y0 + 33, tight);
            using (var b = new SolidBrush(Color.FromArgb(160, accent))) g.DrawString(caller, fontName, b, tx + 2, y0 + 33, tight);
        }
        using (var b = new SolidBrush(YELLOW)) g.DrawString(caller, fontName, b, tx, y0 + 33, tight);
        using (var b = new SolidBrush(Color.FromArgb(235, 240, 246)))
            g.DrawString(text, fontText, b, new RectangleF(tx, y0 + 70, w - (tx - x0) - 16, 36), tight);

        // Одна кнопка «ПРИНЯТЬ» (en «ACCEPT»): единое действие для всех видов — фокус Harness + закрыть.
        answerRect = DrawKey(tx, y0 + h - 38, "enter", En ? "ACCEPT" : "ПРИНЯТЬ", accent);
    }

    // Кнопка-подсказка: ровно по пикселям, срезанный уголок, клавиша слева со значком, нарисованным линиями
    // (символы ↵ и ✕ приходят из запасного шрифта с другими размерами и съезжают), текст по центру.
    RectangleF DrawKey(float fx, float fy, string key, string label, Color color)
    {
        int x = (int)Math.Round(fx), y = (int)Math.Round(fy), hgt = 26, keyW = 26, cut = 7;
        int labelW = (int)Math.Ceiling(g.MeasureString(label, fontKey).Width);
        int wdt = keyW + 10 + labelW + 12;
        var shape = new[]
        {
            new Point(x, y), new Point(x + wdt, y), new Point(x + wdt, y + hgt - cut), new Point(x + wdt - cut, y + hgt), new Point(x, y + hgt),
        };
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using (var b = new SolidBrush(Color.FromArgb(46, color))) g.FillPolygon(b, shape);
        using (var b = new SolidBrush(color)) g.FillRectangle(b, x, y, keyW, hgt);
        using (var p = new Pen(color, 1)) g.DrawPolygon(p, shape);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Значок клавиши, тёмный на цветной клавише.
        float cx = x + keyW / 2f, cy = y + hgt / 2f;
        using (var p = new Pen(Color.FromArgb(10, 12, 16), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (key == "enter")
            {
                g.DrawLines(p, new[] { new PointF(cx + 5, cy - 5), new PointF(cx + 5, cy + 2), new PointF(cx - 5, cy + 2) });
                g.DrawLines(p, new[] { new PointF(cx - 2, cy - 1), new PointF(cx - 5, cy + 2), new PointF(cx - 2, cy + 5) });
            }
            else
            {
                g.DrawLine(p, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                g.DrawLine(p, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
            }
        }
        using (var b = new SolidBrush(color))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near })
            g.DrawString(label, fontKey, b, new RectangleF(x + keyW + 9, y, labelW + 6, hgt + 1), sf);
        g.SmoothingMode = mode;

        return new RectangleF(x, y, wdt, hgt);
    }

    void Present()
    {
        g.Flush(FlushIntention.Sync);
        IntPtr screen = GetDC(IntPtr.Zero);
        var dst = new POINT(Left0, Top0);
        var sz = new SIZE(W, H);
        var src = new POINT(0, 0);
        var blend = new BLEND { op = 0, flags = 0, alpha = 255, format = 1 };
        UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDc, ref src, 0, ref blend, 2);
        ReleaseDC(IntPtr.Zero, screen);
    }

    // Снимок карточки в PNG — проверить вид без экрана.
    public void Snapshot(string path)
    {
        g.Flush(FlushIntention.Sync);
        using (var copy = new Bitmap(canvas)) copy.Save(path, ImageFormat.Png);
    }

    bool disposed;
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (disposing)
            {
                g.Dispose();
                canvas.Dispose();
                if (portrait != null) portrait.Dispose();
                foreach (var f in new[] { fontLabel, fontName, fontText, fontKey }) f.Dispose();
                tight.Dispose();
            }
            SelectObject(memDc, oldBmp);
            DeleteObject(dib);
            DeleteDC(memDc);
        }
        base.Dispose(disposing);
    }
}
