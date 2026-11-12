// Движок десктоп-питомца: окно с настоящей попиксельной прозрачностью (UpdateLayeredWindow), 60 кадров
// в секунду. Поверх кадров героя — процедурная анимация, как в играх: дыхание корпуса, покачивание,
// эффекты шага от мода (присел, вытянулся, подскок, дрожь, приземление), «толчок» при смене позы, тень под
// ногами; герой ходит и бегает вдоль нижнего края окна DeepSeek Harness и сам прогуливается от скуки. Над
// головой — карточка реплики в духе аниме-киберпанка. ЛКМ — реакция героя, ПКМ — меню выбора напарника.
// Слой эффектов: частицы и HUD по клипу и герою (взлом башни у Талии, видоискатель у Романа, «?», «Z z z»…).
// Напарник в кадре: пока Роман снимает, рядом трюкачит второй герой на неоновых лесах.
// Всё медленное — в фоновых потоках: кадры героя грузятся и меряются заранее, state.json читается и
// разбирается отдельно, окно DeepSeek Harness ищется там же. UI-поток только рисует готовое — без рывков.
// Порт DSH: проверка обновлений, самообновление, звезда на GitHub и вызовы Claude Code CLI отключены.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class PetForm : Form
{
    // Холст: герой стоит ногами на линии GROUND по центру; FRAME_G — линия земли в кадрах assets/pet.
    const int W = 520, H = 780, GROUND = H - 24, FRAME_G = 362;
    // На время меню (и напарника в кадре) окно шире: холст выделен сразу с запасом, обычно используется только его левая часть.
    const int CW_MAX = 1100;
    // Кадр шага держится WALK_MS (бега — RUN_MS); за полцикла тело проходит длину шага — опорная нога не скользит.
    const double WALK_MS = 135, RUN_MS = 85;
    // Прогулка дальше этого — бегом.
    const double RUN_FROM = 260;
    // Сессия DeepSeek Harness, за которую держится питомец, молчит дольше этого (с) — можно перейти к другой.
    const double HOLD_SILENCE = 25;
    // После выбора героя в меню прежнего героя от мода не слушаем, пока мод не подтвердит выбор, но не дольше (с).
    const double PICK_HOLD = 30;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte op, flags, alpha, format; }
    [StructLayout(LayoutKind.Sequential)] struct BMIH { public int size, width, height; public short planes, bits; public int compression, imageSize, xppm, yppm, used, important; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BMIH bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    // Мониторинг системы: CPU (GetSystemTimes дельтами) и RAM (GlobalMemoryStatusEx), без PerformanceCounter.
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint LowDateTime, HighDateTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

    readonly IntPtr memDc, dib, oldBmp, bits;
    readonly Bitmap canvas;
    readonly Graphics g;
    readonly Random rnd = new Random();

    static readonly Color RED = Color.FromArgb(255, 59, 71), CYAN = Color.FromArgb(46, 242, 255), YELLOW = Color.FromArgb(252, 238, 10);
    static readonly Color INK = Color.FromArgb(10, 12, 16), PALE = Color.FromArgb(240, 248, 255), MUTED = Color.FromArgb(150, 160, 172);
    // Для эффектов: зелёный «доступ открыт», синий уныния, дым, пыль, светло-голубой пот.
    static readonly Color GREEN = Color.FromArgb(57, 255, 136), BLUE = Color.FromArgb(92, 140, 255), SMOKE = Color.FromArgb(206, 212, 220);
    static readonly Color DUST = Color.FromArgb(214, 208, 196), SWEAT = Color.FromArgb(170, 226, 255), PINK = Color.FromArgb(255, 140, 160);
    readonly List<Font> fonts = new List<Font>();
    readonly Font fontHead, fontText, fMenuHead, fMenuSub, fName, fNameSmall, fRole, fAbout, fStat, fTag, fKey;
    readonly Font fxTiny, fxSmall, fxMono, fxGlyph, fxMid, fxBig, fxEmote, fxZ;
    // Типографский формат — без запасных полей шрифта, строки встают ровно по левой линии.
    readonly StringFormat tight = (StringFormat)StringFormat.GenericTypographic.Clone();
    readonly StringFormat tightLine, aboutFormat;

    // Кадры по 60 к/с и опрос мода/окна DeepSeek Harness раз в четверть секунды — свои таймеры, без PowerShell в цикле.
    readonly System.Windows.Forms.Timer frameTimer = new System.Windows.Forms.Timer(), followTimer = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = new Stopwatch();

    // Адаптивный FPS: спокойная сцена — 30 к/с (CALM_MS), активная — 60 к/с (ACTIVE_MS). Вся анимация считается
    // по времени (dt из clock), поэтому смена интервала не даёт рывков; переключение мгновенное.
    const int ACTIVE_MS = 16, CALM_MS = 30;
    void SetFrameRate(bool active)
    {
        int want = active ? ACTIVE_MS : CALM_MS;
        if (frameTimer.Interval != want) frameTimer.Interval = want;
    }

    public event EventHandler PetClicked;

    public PetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        var bi = new BMIH { size = 40, width = CW_MAX, height = -H, planes = 1, bits = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screen);
        dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDc, dib);
        ReleaseDC(IntPtr.Zero, screen);
        canvas = new Bitmap(CW_MAX, H, CW_MAX * 4, PixelFormat.Format32bppPArgb, bits);
        g = Graphics.FromImage(canvas);

        fontHead = MakeFont("Bahnschrift", 9f, FontStyle.Bold);
        fontText = MakeFont("Bahnschrift", 12.5f, FontStyle.Bold);
        fMenuHead = MakeFont("Bahnschrift", 15f, FontStyle.Bold);
        fMenuSub = MakeFont("Bahnschrift", 8f, FontStyle.Bold);
        fName = MakeFont("Bahnschrift Condensed", 21f, FontStyle.Bold);
        fNameSmall = MakeFont("Bahnschrift Condensed", 16f, FontStyle.Bold);
        fRole = MakeFont("Bahnschrift", 9f, FontStyle.Bold);
        fAbout = MakeFont("Bahnschrift", 8.25f, FontStyle.Regular);
        fStat = MakeFont("Bahnschrift Condensed", 8.5f, FontStyle.Bold);
        fTag = MakeFont("Bahnschrift", 7.5f, FontStyle.Bold);
        fKey = MakeFont("Bahnschrift", 9f, FontStyle.Bold);
        fxTiny = MakeFont("Bahnschrift", 6.5f, FontStyle.Bold);
        fxSmall = MakeFont("Bahnschrift", 7.5f, FontStyle.Bold);
        fxMono = MakeFont("Consolas", 6.75f, FontStyle.Regular);
        fxGlyph = MakeFont("Consolas", 9f, FontStyle.Bold);
        fxMid = MakeFont("Bahnschrift Condensed", 10.5f, FontStyle.Bold);
        fxBig = MakeFont("Bahnschrift Condensed", 16f, FontStyle.Bold);
        fxEmote = MakeFont("Bahnschrift", 21f, FontStyle.Bold);
        fxZ = MakeFont("Bahnschrift", 15f, FontStyle.Bold);
        tightLine = (StringFormat)tight.Clone();
        tightLine.FormatFlags |= StringFormatFlags.NoWrap;
        tightLine.Trimming = StringTrimming.EllipsisCharacter;
        aboutFormat = (StringFormat)tight.Clone();
        aboutFormat.FormatFlags |= StringFormatFlags.LineLimit;
        aboutFormat.Trimming = StringTrimming.EllipsisWord;

        BuildFx();

        // Ошибка в обработчике мыши не должна ронять окно (WinForms показал бы диалог) — пишем в pet-crash.log.
        MouseDown += (s, e) =>
        {
            try
            {
                // ЛКМ по герою/карточке начинает драг. В открытом меню драг выключен: там клики по кнопкам.
                if (e.Button != MouseButtons.Left || menuOpen) return;
                // Координаты курсора берём через PointToScreen(client) — работает и для настоящей мыши,
                // и когда окно двигают синтетическими WM_MOUSEMOVE (нужно для тестов на заблокированном экране).
                dragFrom = PointToScreen(e.Location);
                dragLeft0 = homeLeft;
                dragTop0 = homeTop;
                dragging = true;
                dragMoved = false;
                Capture = true;   // курсор может уйти за пределы окна — MouseUp/Move всё равно дойдут
            }
            catch (Exception ex) { LogCrash("mouse-down", ex); }
        };
        MouseUp += (s, e) => { try { OnUp(e); } catch (Exception ex) { LogCrash("mouse-up", ex); } };
        MouseMove += (s, e) =>
        {
            try
            {
                if (dragging)
                {
                    // Окно следует за курсором; сдвиг > DRAG_THRESHOLD делает нажатие драгом, а не кликом.
                    // PointToScreen(client) = экранная точка курсора (корректно и для настоящей мыши, и для WM_MOUSEMOVE).
                    Point p = PointToScreen(e.Location);
                    int dx = p.X - dragFrom.X, dy = p.Y - dragFrom.Y;
                    if (!dragMoved && (Math.Abs(dx) > DRAG_THRESHOLD || Math.Abs(dy) > DRAG_THRESHOLD)) dragMoved = true;
                    if (dragMoved)
                    {
                        int nl = dragLeft0 + dx, nt = dragTop0 + dy;
                        ClampHome(ref nl, ref nt);
                        homeLeft = nl; homeTop = nt;
                        ApplyHomeArea();
                        SnapToHome();
                    }
                    return;
                }
                mouse = e.Location;
                var want = menuOpen && (HitCard(mouse) >= 0 || OverControl(mouse)) ? Cursors.Hand : Cursors.Default;
                if (Cursor != want) Cursor = want;
            }
            catch (Exception ex) { LogCrash("mouse-move", ex); }
        };
        MouseLeave += (s, e) => { if (!dragging) mouse = new Point(-1, -1); };
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) CloseMenu(); };

        frameTimer.Interval = ACTIVE_MS;
        frameTimer.Tick += (s, e) =>
        {
            try
            {
                double dt = clock.Elapsed.TotalSeconds;
                clock.Restart();
                Tick(dt);
            }
            catch (Exception ex) { LogCrash("frame-tick", ex); }
        };
        followTimer.Interval = 250;
        followTimer.Tick += (s, e) => { try { Follow(); } catch (Exception ex) { Report("follow: " + ex.Message); LogCrash("follow", ex); } };
        loader = new Thread(LoaderLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "pet-frames" };
        loader.Start();
    }

    // Нет начертания (старая Windows) — обычный Bahnschrift, а не случайный запасной шрифт.
    Font MakeFont(string family, float size, FontStyle style)
    {
        var f = new Font(family, size, style);
        if (f.Name != family) { f.Dispose(); f = new Font("Bahnschrift", size, style); }
        fonts.Add(f);
        return f;
    }

    // Слоистое окно без кнопки на панели задач: клики проходят сквозь прозрачные места.
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80; return cp; }
    }

    // Строка в stderr (плагин пишет его в %TEMP%\edgerunners-pet-err.txt).
    static void Report(string text)
    {
        try { Console.Error.WriteLine(text); Console.Error.Flush(); } catch (Exception) { }
    }

    // ---- журнал сбоев: root\assets\pet\pet-crash.log ----
    // Любое исключение UI-потока/клика/меню/отрисовки пишем в файл (видно без консоли) и НЕ роняем окно.
    static string crashPath = "";
    static readonly object crashLock = new object();
    static void LogCrash(string where, Exception e)
    {
        try
        {
            if (string.IsNullOrEmpty(crashPath)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" pid ").Append(Process.GetCurrentProcess().Id).Append(" [").Append(where).Append("] ")
              .Append(e == null ? "?" : e.GetType().FullName + ": " + e.Message).Append('\n');
            if (e != null && !string.IsNullOrEmpty(e.StackTrace)) sb.Append(e.StackTrace).Append('\n');
            lock (crashLock) File.AppendAllText(crashPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    // Окно всегда поверх остальных (HWND_TOPMOST), даже когда Harness свёрнут/не в фокусе.
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    void KeepOnTop()
    {
        try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); }
        catch (Exception e) { LogCrash("topmost", e); }
    }

    // Питомец живёт в правом нижнем углу РАБОЧЕЙ ОБЛАСТИ экрана (учёт панели задач), не окна приложения.
    const int SCREEN_MARGIN_BOTTOM = 32;   // ноги на этом расстоянии от низа рабочей области
    const int SCREEN_RIGHT_MIN = 140;      // центр героя на этом расстоянии от правого края по умолчанию
    // ---- перетаскивание мышью: «дом» питомца = левый верх окна в экранных координатах ----
    int homeLeft, homeTop;                  // текущая домашняя позиция окна (ручная или по умолчанию)
    int savedLeft = int.MinValue, savedTop = int.MinValue;   // из settings.json posX/posY
    bool homeLoaded;                        // InitHome уже вызван
    bool dragging, dragMoved;               // идёт драг; сдвиг уже превысил порог
    Point dragFrom;                         // курсор (экранные координаты) в момент нажатия
    int dragLeft0, dragTop0;                // позиция окна в момент нажатия
    const int DRAG_THRESHOLD = 5;           // сдвиг больше этого = драг, а не клик
    const int WALK_HALF = 160;              // гуляет в этих пределах вокруг домашней точки
    const int HERO_KEEP_X = 90;             // герой целиком остаётся в рабочей области по X
    const int HERO_GROUND_MIN_TOP = 556;    // земля не выше этого от верха рабочей области (макушка видна)
    const int HERO_GROUND_BOTTOM_PAD = 8;   // ноги не ниже этого от низа рабочей области

    void DefaultHome(out int left, out int top)
    {
        var scr = Screen.PrimaryScreen.WorkingArea;
        left = scr.Right - SCREEN_RIGHT_MIN - W / 2;
        top = scr.Bottom - SCREEN_MARGIN_BOTTOM - GROUND;
    }

    // Клампим «дом» так, чтобы сам герой (не прозрачное окно) не уезжал за рабочую область.
    void ClampHome(ref int left, ref int top)
    {
        var scr = Screen.PrimaryScreen.WorkingArea;
        left = Math.Max(scr.Left + HERO_KEEP_X - W / 2, Math.Min(scr.Right - HERO_KEEP_X - W / 2, left));
        top = Math.Max(scr.Top + HERO_GROUND_MIN_TOP - GROUND, Math.Min(scr.Bottom - HERO_GROUND_BOTTOM_PAD - GROUND, top));
    }

    // Дорожка/земля считаются от домашней точки: вокруг неё герой гуляет; меню не выходит за края экрана.
    void ApplyHomeArea()
    {
        var scr = Screen.PrimaryScreen.WorkingArea;
        int cx = homeLeft + W / 2, ground = homeTop + GROUND;
        int lo = Math.Max(scr.Left + HERO_KEEP_X, cx - WALK_HALF);
        int hi = Math.Min(scr.Right - HERO_KEEP_X, cx + WALK_HALF);
        if (hi < lo) { int mid = (lo + hi) / 2; lo = hi = mid; }
        SetArea(lo, hi, ground, scr.Left, scr.Right);
    }

    void SnapToHome() { x = homeLeft + W / 2; wanderTarget = double.NaN; calmSince = DateTime.Now; }

    // Восстановление позиции: сохранённые posX/posY из settings.json, иначе правый нижний угол; затем кламп.
    void InitHome()
    {
        int l, t; DefaultHome(out l, out t);
        if (savedLeft != int.MinValue && savedTop != int.MinValue) { l = savedLeft; t = savedTop; }
        ClampHome(ref l, ref t);
        homeLeft = l; homeTop = t;
        homeLoaded = true;
        SnapToHome();
        ApplyHomeArea();
    }

    // Настройки → «ПОЛОЖЕНИЕ: СБРОСИТЬ»: вернуть в правый нижний угол и забыть сохранённую позицию.
    void ResetHome()
    {
        int l, t; DefaultHome(out l, out t);
        homeLeft = l; homeTop = t;
        savedLeft = savedTop = int.MinValue;
        SnapToHome();
        ApplyHomeArea();
        if (menuOpen) LayoutMenu();
        SaveSettings();
        Report("drag: home reset");
    }

    // ---- мини-режим: вместо полноразмерного героя — маленькая квадратная аватарка-голова ----
    // Видимая часть окна — MINI_W x MINI_H (аватарка + карточка реплики), позиция/драг — те же, что у большого.
    const int MINI_W = 400, MINI_H = 310, MINI_AV = 120, MINI_BOTTOM = 22;
    bool miniOn;                       // settings.json "mini"
    Bitmap miniAvatar;                 // готовый кроп головы (кэш на героя/кадр)
    string miniAvatarKey;
    int renderWindowLeft;              // левый край окна при текущем кадре (для клампа карточки к экрану)

    // Монитор — отдельное окно (см. MonitorForm): размер мини-окна от него не зависит.
    int MiniH() { return MINI_H; }
    int MiniWinLeft() { return homeLeft + W / 2 - MINI_W / 2; }
    int MiniWinTop() { return homeTop + H - MiniH(); }

    // Кроп головы из тесной картинки кадра idle_01: верхняя часть фигуры квадратом (пропорции не искажаются).
    static RectangleF MiniHeadCrop(int w, int h)
    {
        float side = Math.Max(w * 0.62f, h * 0.30f);
        side = Math.Max(40f, Math.Min(side, Math.Min(w, h)));
        float left = (w - side) / 2f, top = 0f;
        if (left < 0) left = 0;
        if (top + side > h) top = h - side;
        return new RectangleF(left, top, side, side);
    }

    Bitmap MiniAvatar()
    {
        var face = cur != null ? cur.Get("idle_01") : null;
        string key = (cur != null ? cur.Folder : "") + "|" + (face != null ? face.GetHashCode() : 0);
        if (miniAvatar != null && key == miniAvatarKey) return miniAvatar;
        if (miniAvatar != null) { miniAvatar.Dispose(); miniAvatar = null; }
        if (face == null || face.Img == null) return null;
        try
        {
            var bmp = new Bitmap(MINI_AV, MINI_AV, PixelFormat.Format32bppPArgb);
            using (var mg = Graphics.FromImage(bmp))
            {
                mg.SmoothingMode = SmoothingMode.AntiAlias;
                mg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var src = MiniHeadCrop(face.Img.Width, face.Img.Height);
                mg.DrawImage(face.Img, new Rectangle(0, 0, MINI_AV, MINI_AV), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            miniAvatar = bmp;
            miniAvatarKey = key;
            return miniAvatar;
        }
        catch (Exception e) { LastError = "mini: " + e.Message; return null; }
    }

    // Аккуратная неоновая рамка вокруг мини-аватарки (свечение + контур + акцент).
    void DrawMiniFrame(int x, int y, int size, double now)
    {
        double pulse = 0.5 + 0.5 * Math.Sin(now * 2.4);
        for (int i = 3; i >= 1; i--)
            using (var pen = new Pen(Color.FromArgb((int)(18 + 20 * pulse), CYAN), i * 2f))
                g.DrawRectangle(pen, x - i, y - i, size + i * 2, size + i * 2);
        using (var pen = new Pen(CYAN, 2)) g.DrawRectangle(pen, x, y, size, size);
        // Уголки-скобки и красный акцент снизу — тот же язык, что у карточек.
        using (var pen = new Pen(YELLOW, 2))
        {
            g.DrawLines(pen, new[] { new PointF(x + 4, y + 14), new PointF(x + 4, y + 4), new PointF(x + 14, y + 4) });
            g.DrawLines(pen, new[] { new PointF(x + size - 14, y + size - 4), new PointF(x + size - 4, y + size - 4), new PointF(x + size - 4, y + size - 14) });
        }
        using (var b = new SolidBrush(RED)) g.FillRectangle(b, x + size - 24, y + size + 3, 24, 3);
    }

    // Отрисовка мини-режима: аватарка стоит на земле (той же, что ноги полного героя), над ней — карточка реплики.
    void RenderMini(FrameData img, string f, double dt, double now)
    {
        int pw = MINI_W, ph = MiniH();
        g.ResetTransform();
        g.ResetClip();
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, Math.Max(pw, lastWidth), Math.Max(ph, lastHeight));
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        fxLeft = 0;   // эффекты клика рисуются в координатах клиента
        int cx = pw / 2;
        int bottom = ph - MINI_BOTTOM;
        int top = bottom - MINI_AV + (int)Math.Round(hopY);
        // Клик/драг ловятся по почти невидимой подложке вокруг аватарки.
        g.FillRectangle(HIT_AREA, new RectangleF(cx - MINI_AV / 2 - 8, top - 8, MINI_AV + 16, MINI_AV + 16));
        var av = MiniAvatar();
        if (av != null) g.DrawImage(av, new Rectangle(cx - MINI_AV / 2, top, MINI_AV, MINI_AV), 0, 0, av.Width, av.Height, GraphicsUnit.Pixel);
        DrawMiniFrame(cx - MINI_AV / 2, top, MINI_AV, now);
        // Панель мониторинга живёт в своём окне (MonitorForm), поэтому в холст питомца больше не рисуется.
        if (taps.Count > 0) try { DrawTaps(dt); } catch (Exception e) { LastError = "tap: " + e.Message; }
        if (DateTime.Now < sayUntil) DrawCard(dt, top, cx, true);
        Present(MiniWinLeft(), MiniWinTop(), pw, ph);
    }

    // ---- мониторинг системы: сэмплирование в фоновом потоке, HUD рисуется в верхней части холста ----
    // Никаких отдельных окон/процессов: GPU опрашивается скрытым nvidia-smi (CreateNoWindow), остальное — WinAPI.
    bool monitorOn = true;                          // settings.json "monitor" (по умолчанию включён)
    int chatsOpen = -1, chatsBusy = -1;             // из state.json (chats/busyChats); -1 — поля нет
    volatile bool netOnline;
    // Числа пишет только фоновый сэмплер, UI-поток их лишь читает; double/long не могут быть volatile,
    // но невыровненных разрывов на x64 нет, а значение обновляется раз в секунду.
    double cpuPct = -1, ramPct = -1, diskPct = -1, gpuPct = -1;
    long ramUsedMb, ramTotalMb, diskFreeGb, diskTotalGb;
    double netDownKb, netUpKb;
    long gpuMemUsedMb, gpuMemTotalMb;
    string gpuName = "";
    volatile bool gpuSmiOk;
    string nvidiaSmiPath;
    DateTime gpuFallbackAt = DateTime.MinValue;
    const int MON_W = 260, MON_H = 228, MON_INTERVAL_MS = 1000, MON_GPU_EVERY_S = 5;
    volatile bool monSampleNow;   // панель только что показали — сэмплировать сразу, не ждать секунду
    // SPEND: траты на токены за сегодня из state.json (spendRub/spendBudgetRub). spendRub < 0 — поля нет.
    double spendRub = -1, spendBudgetRub = 100;
    string spendCurrency = "";
    // BALANCE: остаток на балансе в рублях из state.json (balanceRub); < 0 — поля нет (рисуем «—»).
    double balanceRub = -1;
    string balanceCurrency = "";

    static ulong FileTimeValue(FILETIME f) { return ((ulong)f.HighDateTime << 32) | f.LowDateTime; }

    // Фоновый поток: CPU/RAM/DISK/NET раз в секунду и GPU раз в 5 с — ТОЛЬКО пока панель мониторинга видима.
    // При monitor=false поток спит без сэмплирования (почти нулевая нагрузка), UI-поток читает готовые числа.
    void SamplerLoop()
    {
        ulong prevIdle = 0, prevKernel = 0, prevUser = 0;
        long prevRx = -1, prevTx = -1;
        DateTime prevNetAt = DateTime.MinValue;
        int tick = 0;
        while (!closing)
        {
            try
            {
                bool visible = monitorOn && monForm != null && monForm.Visible;
                if (visible)
                {
                    bool sampleGpu = tick % MON_GPU_EVERY_S == 0 || monSampleNow;
                    monSampleNow = false;
                    SampleCpu(ref prevIdle, ref prevKernel, ref prevUser);
                    SampleRam();
                    SampleDisk();
                    SampleNet(ref prevRx, ref prevTx, ref prevNetAt);
                    if (sampleGpu) SampleGpu();
                    tick++;
                }
                else tick = 0;
            }
            catch (Exception e) { LastError = "monitor: " + e.Message; }
            for (int i = 0; i < MON_INTERVAL_MS / 100 && !closing; i++) Thread.Sleep(100);
        }
    }

    void SampleCpu(ref ulong idle, ref ulong kernel, ref ulong user)
    {
        FILETIME fi, fk, fu;
        if (!GetSystemTimes(out fi, out fk, out fu)) return;
        ulong i = FileTimeValue(fi), k = FileTimeValue(fk), u = FileTimeValue(fu);
        if (kernel != 0 || user != 0)
        {
            ulong di = i - idle, dk = k - kernel, du = u - user;
            ulong total = dk + du;   // kernel уже включает idle
            if (total > 0) cpuPct = Math.Round(100.0 * (total - Math.Min(di, total)) / total, 0);
        }
        idle = i; kernel = k; user = u;
    }

    void SampleRam()
    {
        var s = new MEMORYSTATUSEX();
        s.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (!GlobalMemoryStatusEx(ref s)) return;
        ramPct = s.dwMemoryLoad;
        ramTotalMb = (long)(s.ullTotalPhys / (1024UL * 1024UL));
        ramUsedMb = ramTotalMb - (long)(s.ullAvailPhys / (1024UL * 1024UL));
    }

    void SampleDisk()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            if (!d.IsReady) return;
            long total = d.TotalSize, free = d.AvailableFreeSpace;
            diskPct = total > 0 ? Math.Round(100.0 * (total - free) / total, 0) : 0;
            diskFreeGb = free / (1024L * 1024 * 1024);
            diskTotalGb = total / (1024L * 1024 * 1024);
        }
        catch (Exception) { }
    }

    void SampleNet(ref long prevRx, ref long prevTx, ref DateTime prevAt)
    {
        long rx = 0, tx = 0;
        bool up = false;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                    up = true;
                }
                catch (Exception) { }
            }
            var now = DateTime.Now;
            if (prevRx >= 0 && (now - prevAt).TotalMilliseconds > 250)
            {
                double dt = (now - prevAt).TotalSeconds;
                netDownKb = Math.Max(0, (rx - prevRx) / 1024.0 / dt);
                netUpKb = Math.Max(0, (tx - prevTx) / 1024.0 / dt);
            }
            prevRx = rx; prevTx = tx; prevAt = now;
            netOnline = up && NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception) { }
    }

    static string FindNvidiaSmi()
    {
        var candidates = new List<string>();
        try { candidates.Add(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")); } catch (Exception) { }
        try { candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")); } catch (Exception) { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { string d = dir.Trim().Trim('"'); if (d.Length > 0) candidates.Add(Path.Combine(d, "nvidia-smi.exe")); } catch (Exception) { }
        foreach (var f in candidates)
            try { if (File.Exists(f)) return f; } catch (Exception) { }
        return "";
    }

    // GPU: nvidia-smi раз в ~3 с в скрытом процессе (без окна/консоли); иначе — имя/VRAM через WMI и «—».
    void SampleGpu()
    {
        if (nvidiaSmiPath == null) nvidiaSmiPath = FindNvidiaSmi();
        bool ok = false;
        if (nvidiaSmiPath.Length > 0 && File.Exists(nvidiaSmiPath))
        {
            try
            {
                var psi = new ProcessStartInfo(nvidiaSmiPath)
                {
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    if (p.WaitForExit(2500))
                    {
                        var parts = (line ?? "").Split(',');
                        double gu; long mu, mt;
                        if (parts.Length >= 3 &&
                            double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out gu) &&
                            long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mu) &&
                            long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out mt))
                        {
                            gpuPct = gu; gpuMemUsedMb = mu; gpuMemTotalMb = mt; ok = true;
                        }
                    }
                    else { try { p.Kill(); } catch (Exception) { } }
                }
            }
            catch (Exception) { }
        }
        gpuSmiOk = ok;
        if (ok) return;
        // Fallback WMI — не чаще раза в 30 с (запрос не бесплатный), в фоновом потоке.
        if ((DateTime.Now - gpuFallbackAt).TotalSeconds < 30 && gpuName != "") return;
        gpuFallbackAt = DateTime.Now;
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    string nm = Convert.ToString(mo["Name"]);
                    if (string.IsNullOrEmpty(nm)) continue;
                    bool nv = nm.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (gpuName == "" || nv)
                    {
                        gpuName = nm;
                        object ram = mo["AdapterRAM"];
                        if (ram != null) try { gpuMemTotalMb = (long)(Convert.ToUInt64(ram) / (1024UL * 1024UL)); } catch (Exception) { }
                        if (nv) break;
                    }
                }
        }
        catch (Exception) { }
    }

    static string MonRate(double kb)
    {
        if (kb <= 0) return "0";
        return kb >= 1024 ? (kb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s" : kb.ToString("0", CultureInfo.InvariantCulture) + " KB/s";
    }

    // Подпись данных HUD: окно панели перерисовывается только когда она меняется (раз в 1 с или при показе).
    string MonSignature()
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:0}|{1:0}|{2}|{3}|{4}|{5}|{6:0.0}|{7:0}|{8:0}|{9}:{10}|{11}",
            cpuPct, ramPct, ramUsedMb, ramTotalMb, diskFreeGb, diskTotalGb, diskPct,
            gpuSmiOk ? (gpuPct + 1000) * 1000 + gpuMemUsedMb : -1, gpuMemTotalMb,
            netOnline ? 1 : 0, ((int)netDownKb) + "x" + ((int)netUpKb), chatsOpen + ":" + chatsBusy)
            + "|" + spendRub.ToString("0.0", CultureInfo.InvariantCulture) + "|" + spendBudgetRub.ToString("0.0", CultureInfo.InvariantCulture)
            + "|" + balanceRub.ToString("0.0", CultureInfo.InvariantCulture);
    }

    // Строка HUD: подпись слева, тонкий бар (если pct >= 0) и значение справа. Значение «—» при отсутствии данных.
    // barColor — цвет заполнения для трёхцветных шкал (SPEND); без него — бирюза/красный по загрузке.
    void MonRow(Graphics tg, int x, int y, string label, double pct, string value, Color? barColor = null)
    {
        using (var b = new SolidBrush(Color.FromArgb(225, 198, 214, 224))) tg.DrawString(label, fKey, b, x + 10, y + 1, tight);
        if (pct >= 0)
        {
            float bx = x + 52, bw = MON_W - 52 - 96;
            using (var b = new SolidBrush(Color.FromArgb(90, 44, 74, 84))) tg.FillRectangle(b, bx, y + 6, bw, 5);
            double k = Math.Max(0, Math.Min(1, pct / 100.0));
            Color col = barColor.HasValue ? barColor.Value : (pct >= 85 ? RED : CYAN);
            using (var b = new SolidBrush(col)) tg.FillRectangle(b, bx, y + 6, (float)(bw * k), 5);
        }
        float tw = tg.MeasureString(value, fKey, PointF.Empty, tight).Width;
        using (var b = new SolidBrush(Color.FromArgb(235, 232, 238, 244))) tg.DrawString(value, fKey, b, x + MON_W - 10 - tw, y, tight);
    }

    // Компактная полупрозрачная панель в стиле карточки/меню: шапка «// SYS.MON» и 7 строк (SPEND — снизу).
    // Рисуется в произвольный Graphics: окно мониторинга (MonitorForm) передаёт сюда свой холст.
    void DrawMonitor(Graphics tg, int x, int y)
    {
        float c = 12;
        var pts = new[]
        {
            new PointF(x, y), new PointF(x + MON_W - c, y), new PointF(x + MON_W, y + c),
            new PointF(x + MON_W, y + MON_H), new PointF(x, y + MON_H),
        };
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(pts);
            for (int i = 3; i >= 1; i--)
                using (var p = new Pen(Color.FromArgb(16, CYAN), i * 3f)) tg.DrawPath(p, path);
            using (var fill = new SolidBrush(Color.FromArgb(198, 8, 12, 16))) tg.FillPath(fill, path);
            using (var p = new Pen(Color.FromArgb(170, CYAN), 1)) tg.DrawPath(p, path);
        }
        using (var b = new SolidBrush(CYAN)) tg.DrawString("// SYS.MON", fMenuSub, b, x + 10, y + 6);
        using (var b = new SolidBrush(Color.FromArgb(120, CYAN))) tg.FillRectangle(b, x + 10, y + 19, MON_W - 20, 1);
        int ry = y + 24;
        MonRow(tg, x, ry, "CPU", cpuPct, cpuPct >= 0 ? ((int)Math.Round(cpuPct)).ToString(CultureInfo.InvariantCulture) + "%" : "—");
        ry += 24;
        MonRow(tg, x, ry, "RAM", ramPct, ramTotalMb > 0
            ? ((ramUsedMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "/" + (ramTotalMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB")
            : "—");
        ry += 24;
        string gpuVal;
        if (gpuSmiOk)
            gpuVal = ((int)Math.Round(Math.Max(0, gpuPct))).ToString(CultureInfo.InvariantCulture) + "% " +
                     (gpuMemUsedMb / 1024).ToString(CultureInfo.InvariantCulture) + "/" + (gpuMemTotalMb / 1024).ToString(CultureInfo.InvariantCulture) + "G";
        else if (!string.IsNullOrEmpty(gpuName))
        {
            string nm = gpuName.Length > 18 ? gpuName.Substring(0, 18) : gpuName;
            gpuVal = nm + " —";
        }
        else gpuVal = "—";
        MonRow(tg, x, ry, "GPU", gpuSmiOk ? gpuPct : -1, gpuVal);
        ry += 24;
        MonRow(tg, x, ry, "DISK", diskPct, diskTotalGb > 0
            ? diskFreeGb.ToString(CultureInfo.InvariantCulture) + " GB free"
            : "—");
        ry += 24;
        MonRow(tg, x, ry, "NET", -1, (netOnline ? "ON " : "OFF ") + "↓" + MonRate(netDownKb) + " ↑" + MonRate(netUpKb));
        ry += 24;
        string chats = chatsOpen < 0 ? "—" : (chatsBusy >= 0 ? chatsOpen.ToString(CultureInfo.InvariantCulture) + "/" + chatsBusy.ToString(CultureInfo.InvariantCulture) : chatsOpen.ToString(CultureInfo.InvariantCulture));
        MonRow(tg, x, ry, "CHATS", -1, chats);
        ry += 24;
        // SPEND: «12,4 ₽» + тонкая шкала spendRub/spendBudgetRub; нет поля spendRub — «—» без шкалы.
        if (spendRub < 0) MonRow(tg, x, ry, "SPEND", -1, "—");
        else
        {
            double budget = spendBudgetRub > 0 ? spendBudgetRub : 100;
            double pct = Math.Max(0, Math.Min(100, spendRub / budget * 100));
            // <60% — бирюза, 60–90% — жёлтый, >90% — красный (порог 87% из ТЗ — жёлтый, 95% — красный).
            Color col = pct > 90 ? RED : pct >= 60 ? YELLOW : CYAN;
            MonRow(tg, x, ry, "SPEND", pct, SpendText(spendRub), col);
        }
        ry += 24;
        // BALANCE: остаток на балансе в рублях, без шкалы (просто значение); нет поля — «—».
        MonRow(tg, x, ry, "BALANCE", -1, balanceRub < 0 ? "—" : BalanceText(balanceRub));
    }

    // «12,4 ₽» / «12345 ₽» — русская запятая, одна десятая; от 1000 ₽ — целыми рублями.
    static string MoneyText(double rub)
    {
        string n = rub >= 1000 ? Math.Round(rub).ToString("0", CultureInfo.InvariantCulture)
                               : rub.ToString("0.0", CultureInfo.InvariantCulture);
        return n.Replace('.', ',') + " ₽";
    }

    static string SpendText(double rub) { return MoneyText(rub); }
    static string BalanceText(double rub) { return MoneyText(rub); }

    // ---- отдельное окно панели SYS.MON: второй layered/topmost Hwnd в том же процессе ----
    // Своё окно и своя позиция (monX/monY), поэтому драг панели не трогает героя и наоборот.
    int monX = int.MinValue, monY = int.MinValue;
    MonitorForm monForm;

    void EnsureMonPosition()
    {
        if (monX != int.MinValue && monY != int.MinValue) { ClampMonToScreen(); return; }
        // Первое включение: чуть левее-выше питомца; если слева нет места — справа. Всё в пределах WorkingArea.
        var wa = Screen.FromPoint(new Point(homeLeft + 20, homeTop + 20)).WorkingArea;
        int x = homeLeft - MON_W - 12, y = homeTop + 40;
        if (x < wa.Left) x = homeLeft + W + 12;
        monX = x; monY = y;
        ClampMonToScreen();
    }

    void ClampMonToScreen()
    {
        var wa = Screen.FromPoint(new Point(monX + MON_W / 2, monY + MON_H / 2)).WorkingArea;
        monX = Math.Max(wa.Left, Math.Min(wa.Right - MON_W, monX));
        monY = Math.Max(wa.Top, Math.Min(wa.Bottom - MON_H, monY));
    }

    // Показать/спрятать окно панели; позиция сохраняется и при повторном включении возвращается на место.
    void ShowMonitor()
    {
        EnsureMonPosition();
        monSampleNow = true;
        if (monForm == null) monForm = new MonitorForm(this);
        if (!monForm.Visible) monForm.Show();
        monForm.MoveTo(monX, monY, false);
        monForm.Render();
    }

    void HideMonitor()
    {
        if (monForm != null && monForm.Visible) monForm.Hide();
    }

    // Позиция панели: клампится в WorkingArea, при save=true сразу пишется в settings.json.
    void SetMonPos(int x, int y, bool save)
    {
        monX = x; monY = y;
        ClampMonToScreen();
        if (save) SaveSettings();
    }

    // Второе окно: тот же стиль и тот же рендер HUD, но свой layered-Hwnd; ЛКМ тянет только его.
    sealed class MonitorForm : Form
    {
        readonly PetForm owner;
        readonly IntPtr memDc, dib, oldBmp, bits;
        readonly Bitmap canvas;
        readonly Graphics g;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool dragging;
        Point dragFrom;
        int dragLeft0, dragTop0;

        public MonitorForm(PetForm o)
        {
            owner = o;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(MON_W, MON_H);
            var bi = new BMIH { size = 40, width = MON_W, height = -MON_H, planes = 1, bits = 32 };
            IntPtr screen = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screen);
            dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBmp = SelectObject(memDc, dib);
            ReleaseDC(IntPtr.Zero, screen);
            canvas = new Bitmap(MON_W, MON_H, MON_W * 4, PixelFormat.Format32bppPArgb, bits);
            g = Graphics.FromImage(canvas);
            Cursor = Cursors.SizeAll;
            // HUD обновляется раз в секунду; окно перерисовывается только когда данные реально изменились.
            timer.Interval = 1000;
            timer.Tick += (s, e) => Render(false);
            MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                dragging = true;
                dragFrom = PointToScreen(e.Location);
                dragLeft0 = Left; dragTop0 = Top;
                Capture = true;
            };
            MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var p = PointToScreen(e.Location);
                MoveTo(dragLeft0 + (p.X - dragFrom.X), dragTop0 + (p.Y - dragFrom.Y), false);
            };
            MouseUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                Capture = false;
                owner.SetMonPos(Left, Top, true);
            };
        }

        // Слойное окно без активации: клик по панели не забирает фокус у Harness.
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; return cp; } // LAYERED | TOOLWINDOW | NOACTIVATE
        }

        public bool Dragging { get { return dragging; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            timer.Start();
            Render(true);
        }

        // Пока окно скрыто (monitor=false) таймер не работает и ничего не рисуется; при показе — сразу перерисовать.
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                timer.Start();
                Render(true);
            }
            else timer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop();
                timer.Dispose();
                if (g != null) g.Dispose();
                if (canvas != null) canvas.Dispose();
                if (memDc != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
            }
            base.Dispose(disposing);
        }

        // Двигает ТОЛЬКО окно панели (клампится в WorkingArea); позицию запоминаем в PetForm.monX/monY.
        public void MoveTo(int x, int y, bool save)
        {
            var wa = Screen.FromPoint(new Point(x + MON_W / 2, y + MON_H / 2)).WorkingArea;
            x = Math.Max(wa.Left, Math.Min(wa.Right - MON_W, x));
            y = Math.Max(wa.Top, Math.Min(wa.Bottom - MON_H, y));
            owner.monX = x; owner.monY = y;
            if (Left != x || Top != y) Location = new Point(x, y);
            Present();
            if (save) owner.SaveSettings();
        }

        string lastSig;

        public void Render() { Render(true); }

        // force=false: перерисовать только если данные HUD изменились (иначе окно остаётся как есть, без мерцания).
        public void Render(bool force)
        {
            if (!Visible || IsDisposed || !IsHandleCreated) return;
            try
            {
                string sig = owner.MonSignature();
                if (!force && sig == lastSig) return;
                lastSig = sig;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, MON_W, MON_H);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                owner.DrawMonitor(g, 0, 0);
                Present();
            }
            catch (Exception e) { owner.LastError = "mon: " + e.Message; }
        }

        void Present()
        {
            if (IsDisposed || !IsHandleCreated) return;
            g.Flush(FlushIntention.Sync);
            IntPtr screen = GetDC(IntPtr.Zero);
            var dst = new POINT(Left, Top);
            var sz = new SIZE(MON_W, MON_H);
            var src = new POINT(0, 0);
            var blend = new BLEND { op = 0, flags = 0, alpha = 255, format = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDc, ref src, 0, ref blend, 2);
            ReleaseDC(IntPtr.Zero, screen);
        }

        // Снимок панели для тестов: preview/mon-live.png.
        public void SaveCanvas(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var copy = new Bitmap(MON_W, MON_H, PixelFormat.Format32bppArgb);
                using (var cg = Graphics.FromImage(copy))
                {
                    cg.CompositingMode = CompositingMode.SourceCopy;
                    cg.DrawImage(canvas, new Rectangle(0, 0, MON_W, MON_H), new Rectangle(0, 0, MON_W, MON_H), GraphicsUnit.Pixel);
                }
                copy.Save(path, ImageFormat.Png);
                copy.Dispose();
            }
            catch (Exception e) { owner.LastError = "mon: " + e.Message; }
        }
    }

    // ---- heartbeat для плагина: assets/pet/pet.beat {"pid":…,"at":<ms>} ----
    void WriteBeat()
    {
        try
        {
            if (string.IsNullOrEmpty(beatPath)) return;
            long at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            string text = "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"at\":" + at + "}";
            string tmp = beatPath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(beatPath)) File.Replace(tmp, beatPath, null, true);
                else File.Move(tmp, beatPath);
            }
            catch (Exception)
            {
                try { File.Delete(beatPath); } catch (Exception) { }
                File.Move(tmp, beatPath);
            }
        }
        catch (Exception) { }
    }

    // Раз в 5 с, пока процесс жив (в том числе если питомец спрятан): протухший beat = плагин перезапустит.
    void BeatLoop()
    {
        int n = 0;
        while (!closing)
        {
            if (n++ > 0) WriteBeat();
            for (int i = 0; i < 50 && !closing; i++) Thread.Sleep(100);
        }
    }

    void RemoveBeat()
    {
        try
        {
            if (string.IsNullOrEmpty(beatPath) || !File.Exists(beatPath)) return;
            // Beat другого (живого) процесса не трогаем.
            var o = Json.Parse(ReadShared(beatPath)) as Dictionary<string, object>;
            if (o != null && (int)Num(Val(o, "pid")) == Process.GetCurrentProcess().Id) File.Delete(beatPath);
        }
        catch (Exception) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing = true;
        // Позицию (и прочие настройки) сохраняем при штатном выходе: плагин перезапустит питомца на том же месте.
        if (homeLoaded) SaveSettings();
        RemoveBeat();
        base.OnFormClosed(e);
    }

    // ---- настройки: панель в меню, файл assets/pet/settings.json читают ещё мод (язык) и звонок (звук) ----
    string settingsPath, lang = "ru";
    bool callOn = true, fxOn = true, wanderOn = true;
    // Громкость звонка 0..10; реплики: 0 — часто, 1 — редко, 2 — только в ответ на клик.
    int volume = 4, linesMode;
    // Размер героя в процентах: читает и фоновый загрузчик кадров.
    volatile int sizePct = 100;
    static readonly int[] SIZES = { 80, 100, 120, 140 };
    // Проба громкости из меню — звук играется ВНУТРИ процесса (System.Media.SoundPlayer), без внешних
    // PowerShell/окон: спавн powershell из UI-потока давал лаги и мог уронить окно.
    System.Media.SoundPlayer testPlayer;

    string Tr(string ru, string en) { return lang == "en" ? en : ru; }

    void LoadSettings()
    {
        try
        {
            var o = Json.Parse(File.ReadAllText(settingsPath, Encoding.UTF8).TrimStart('\uFEFF')) as Dictionary<string, object>;
            if (o == null) return;
            lang = Str(o, "lang") == "en" ? "en" : "ru";
            callOn = Flag(o, "call", true);
            fxOn = Flag(o, "effects", true);
            wanderOn = Flag(o, "wander", true);
            if (Val(o, "volume") != null) volume = Math.Max(0, Math.Min(10, (int)Num(Val(o, "volume"))));
            if (Val(o, "lines") != null) linesMode = Math.Max(0, Math.Min(2, (int)Num(Val(o, "lines"))));
            if (Val(o, "size") != null && Array.IndexOf(SIZES, (int)Num(Val(o, "size"))) >= 0) sizePct = (int)Num(Val(o, "size"));
            autoUpdate = Str(o, "updates") == "auto";
            starAsked = Flag(o, "starAsked", false);
            firstRun = Str(o, "firstRun") ?? "";
            if (Val(o, "posX") != null) savedLeft = (int)Num(Val(o, "posX"));
            if (Val(o, "posY") != null) savedTop = (int)Num(Val(o, "posY"));
            miniOn = Flag(o, "mini", false);
            monitorOn = Flag(o, "monitor", true);
            if (Val(o, "monX") != null) monX = (int)Num(Val(o, "monX"));
            if (Val(o, "monY") != null) monY = (int)Num(Val(o, "monY"));
            long un;
            if (long.TryParse(Str(o, "updNotice") ?? "", out un)) updNoticeTicks = un;
        }
        catch (Exception) { }
    }

    static bool Flag(Dictionary<string, object> o, string k, bool def) { var v = Val(o, k); return v is bool ? (bool)v : def; }

    void SaveSettings()
    {
        // Позицию пишем всегда (posX/posY — левый верх окна): после перетаскивания плагин/приложение
        // перезапускают питомца, и он встаёт на то же место (ResetHome возвращает угол).
        int px = homeLeft, py = homeTop;
        if (!homeLoaded) { int dl, dt; DefaultHome(out dl, out dt); px = dl; py = dt; }
        string text = "{\"lang\":\"" + lang + "\",\"call\":" + (callOn ? "true" : "false") + ",\"volume\":" + volume + ",\"lines\":" + linesMode
            + ",\"effects\":" + (fxOn ? "true" : "false") + ",\"wander\":" + (wanderOn ? "true" : "false") + ",\"size\":" + sizePct
            + ",\"posX\":" + px + ",\"posY\":" + py + ",\"mini\":" + (miniOn ? "true" : "false") + ",\"monitor\":" + (monitorOn ? "true" : "false")
            + ",\"monX\":" + monX + ",\"monY\":" + monY
            + ",\"updates\":\"" + (autoUpdate ? "auto" : "ask") + "\",\"starAsked\":" + (starAsked ? "true" : "false") + ",\"firstRun\":\"" + firstRun + "\",\"updNotice\":\"" + updNoticeTicks + "\"}";
        try { File.WriteAllText(settingsPath, text, new UTF8Encoding(false)); }
        catch (Exception e) { Report("settings: " + e.Message); }
    }

    // Проба звука при смене громкости: короткий встроенный рингтон, прошлая проба обрывается.
    // Никаких внешних процессов: SoundPlayer грузит wav в память и играет его асинхронно.
    void PlayTest()
    {
        try
        {
            if (testPlayer != null)
            {
                try { testPlayer.Stop(); } catch (Exception) { }
                try { testPlayer.Dispose(); } catch (Exception) { }
                testPlayer = null;
            }
            if (volume <= 0) return;
            string wav = Path.Combine(root, @"assets\sounds\ring.wav");
            if (!File.Exists(wav)) return;
            testPlayer = new System.Media.SoundPlayer(wav);
            testPlayer.Load();
            testPlayer.Play();
        }
        catch (Exception e) { Report("test: " + e.Message); LogCrash("play-test", e); }
    }

    // ---- обновления и звезда на GitHub ----
    // Порт DSH: весь этот раздел выключен. Сеть не трогаем (ни проверку версий, ни gh, ни страницу
    // репозитория) и CLI Claude Code не запускаем; код оригинала оставлен ниже для сверки, но за
    // рантайм-флагом, чтобы компилятор не считал его недостижимым. Меню настроек обновления/звезду не показывает.
    static readonly bool NET_OFF = true;
    // Раз в час сверяем version из .claude-plugin/plugin.json с репозиторием. Новая версия: «АВТО» — питомец сам
    // запускает claude plugin update, «СПРАШИВАТЬ» — говорит об этом, клик по герою обновляет. В первый день один раз
    // просит звезду на GitHub: только по клику ставит её через GitHub CLI (gh) с аккаунта человека, а без gh или без
    // входа в него открывает страницу. Пока висит такая карточка, реплики мода ждут, а клик по герою — ответ на неё.
    const string REPO = "https://github.com/Alexanderadon/edgerunners";
    const string REMOTE_MANIFEST = "https://raw.githubusercontent.com/Alexanderadon/edgerunners/main/.claude-plugin/plugin.json";
    bool autoUpdate, starAsked, updating, downloaded, updatedOk;
    string firstRun = "", localVer = "", handledVer = "";
    // Версия на GitHub (пишет фоновый поток; "" — не достучались), проверка по кнопке, итог обновления (1 / -1).
    volatile string remoteVer;
    volatile bool checkNow, checkReport;
    volatile int updateDone;
    // Итог звезды через gh: 1 поставлена, -1 нет gh или входа; starring, пока ждём ответа.
    volatile int starDone;
    bool starring;
    // Когда последний раз говорили об обновлении (или обновились): о новой версии не чаще раза в сутки.
    long updNoticeTicks;
    DateTime petStart = DateTime.Now, nextNotice = DateTime.MaxValue, noticeUntil = DateTime.MinValue;
    Action noticeAct;

    string ReadLocalVersion()
    {
        try
        {
            var o = Json.Parse(File.ReadAllText(Path.Combine(root, @".claude-plugin\plugin.json"), Encoding.UTF8).TrimStart('﻿')) as Dictionary<string, object>;
            return (o == null ? null : Str(o, "version")) ?? "";
        }
        catch (Exception) { return ""; }
    }

    void UpdateLoop()
    {
        if (NET_OFF) return;
        Thread.Sleep(20000);
        while (!closing)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var wc = new System.Net.WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "edgerunners-pet";
                    var o = Json.Parse(wc.DownloadString(REMOTE_MANIFEST + "?t=" + DateTime.UtcNow.Ticks).TrimStart('﻿')) as Dictionary<string, object>;
                    remoteVer = (o == null ? null : Str(o, "version")) ?? "";
                }
            }
            catch (Exception e) { remoteVer = ""; Report("update check: " + e.Message); }
            for (int i = 0; i < 360 && !closing && !checkNow; i++) Thread.Sleep(10000);
            checkNow = false;
        }
    }

    static bool Newer(string a, string b)
    {
        Version x, y;
        return Version.TryParse(a ?? "", out x) && Version.TryParse(b ?? "", out y) && x > y;
    }

    // Claude Code уже скачал эту версию сам (его автообновление): рядом с нашей папкой версии есть такая же или новее.
    bool AlreadyDownloaded(string v)
    {
        try
        {
            string me = root.TrimEnd('\\', '/');
            Version mine, x;
            if (!Version.TryParse(Path.GetFileName(me), out mine)) return false;
            foreach (var d in Directory.GetDirectories(Path.GetDirectoryName(me)))
                if (Version.TryParse(Path.GetFileName(d), out x) && !Newer(v, x.ToString())) return true;
        }
        catch (Exception) { }
        return false;
    }

    // Раз в четверть секунды (UI-поток): итог обновления, свежая версия с GitHub, карточки.
    void UpdateTick()
    {
        if (NET_OFF) return;
        var t = DateTime.Now;
        int star = starDone;
        if (star != 0)
        {
            starDone = 0;
            starring = false;
            if (star > 0) Notify(Tr("Звезда стоит, спасибо! Это топливо для новых героев", "Star is in, thanks! It fuels new heroes"), null);
            else
            {
                OpenRepo();
                Notify(Tr("Открыл GitHub: звезда справа вверху. Спасибо!", "Opened GitHub: the Star button is at the top right. Thanks!"), null);
            }
        }
        int done = updateDone;
        if (done != 0)
        {
            updateDone = 0;
            updating = false;
            updatedOk = done > 0;
            nextNotice = DateTime.MaxValue;
            if (done > 0) { updNoticeTicks = t.Ticks; SaveSettings(); }
            if (done > 0)
                Notify(Tr("Обновился до v" + handledVer + ", перезапусти DeepSeek Harness. Кликни, поставлю звезду на GitHub",
                          "Updated to v" + handledVer + ", restart DeepSeek Harness. Click me to star the mod on GitHub"), StarRepo);
            else
                Notify(Tr("Не вышло обновиться. Набери /plugin → Marketplaces → edgerunners → Update",
                          "Update failed. Run /plugin → Marketplaces → edgerunners → Update"), null);
            return;
        }
        string rv = remoteVer;
        if (rv != null && !updating)
        {
            if (rv == "")
            {
                if (checkReport) { checkReport = false; Notify(Tr("GitHub не отвечает, попробуй позже", "GitHub isn't answering, try later"), null); }
            }
            else if (Newer(rv, localVer))
            {
                if (rv != handledVer)
                {
                    handledVer = rv;
                    bool manual = checkReport;
                    checkReport = false;
                    downloaded = AlreadyDownloaded(rv);
                    if (!downloaded && autoUpdate) StartUpdate();
                    else
                    {
                        // Две версии за день не превращаются в две тревоги подряд: следующая карточка не раньше чем
                        // через сутки после прошлой. Проверка по кнопке отвечает сразу, кнопка в меню видна всегда.
                        var at = checkNow || t > petStart.AddSeconds(40) ? t : petStart.AddSeconds(40);
                        var calm = new DateTime(updNoticeTicks).AddHours(24);
                        nextNotice = !manual && calm > at ? calm : at;
                    }
                }
            }
            else if (checkReport) { checkReport = false; Notify(Tr("У тебя последняя версия: v" + localVer, "You have the latest version: v" + localVer), null); }
        }
        if (t >= nextNotice && !menuOpen && t >= noticeUntil)
        {
            updNoticeTicks = t.Ticks;
            SaveSettings();
            if (downloaded)
            {
                nextNotice = DateTime.MaxValue;
                Notify(Tr("Новая версия v" + handledVer + " уже скачана, перезапусти DeepSeek Harness", "Version v" + handledVer + " is downloaded, restart DeepSeek Harness"), null);
            }
            else
            {
                nextNotice = t.AddHours(24);
                Notify(Tr("Вышла новая версия мода: v" + handledVer + ". Кликни меня, обновлю", "Mod update v" + handledVer + " is out. Click me and I'll install it"), StartUpdate);
            }
        }
        // Звезда: один раз, в первый же день, когда герой минут десять поработал рядом, и не поверх обновления.
        if (!starAsked && !updating && nextNotice == DateTime.MaxValue && !menuOpen && t >= noticeUntil && t > petStart.AddMinutes(10))
        {
            starAsked = true;
            SaveSettings();
            Notify(Tr("Нравится, как мы работаем? Кликни меня, поставлю моду звезду на GitHub",
                      "Enjoying our work together? Click me and I'll star the mod on GitHub"), StarRepo);
        }
    }

    // Карточка, которую не перебьют реплики мода; act — что сделать по клику, пока она видна.
    void Notify(string text, Action act)
    {
        var t = DateTime.Now;
        if (t >= sayUntil) sayAt = t;
        line = text;
        lineAt = t;
        pendingLine = null;
        sayUntil = noticeUntil = t.AddSeconds(act != null ? 20 : 12);
        noticeAct = act;
    }

    // Клик по герою, пока видна карточка: её действие. true — клик съеден.
    bool NoticeClick()
    {
        if (menuOpen || DateTime.Now >= noticeUntil) return false;
        var act = noticeAct;
        noticeAct = null;
        noticeUntil = DateTime.Now;
        if (act == null) return false;
        act();
        return true;
    }

    void StartUpdate()
    {
        if (NET_OFF) return;
        if (updating) return;
        updating = true;
        nextNotice = DateTime.MaxValue;
        if (!autoUpdate || noticeAct != null || DateTime.Now < noticeUntil) Notify(Tr("Обновляюсь… это до минуты", "Updating… up to a minute"), null);
        new Thread(() =>
        {
            string exe = FindClaudeCli();
            bool ok = RunClaude(exe, "plugin marketplace update edgerunners") == 0 && RunClaude(exe, "plugin update edgerunners@edgerunners") == 0;
            updateDone = ok ? 1 : -1;
        }) { IsBackground = true, Name = "pet-upgrade" }.Start();
    }

    // CLI Claude Code: тот, что запущен (в приложении он лежит в папке claude-code), иначе ~/.local/bin, иначе из PATH.
    static string FindClaudeCli()
    {
        if (NET_OFF) return null;
        try
        {
            foreach (var p in Process.GetProcessesByName("claude"))
                try
                {
                    string f = p.MainModule.FileName;
                    if (f.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) >= 0 || f.IndexOf(@"\.local\bin\", StringComparison.OrdinalIgnoreCase) >= 0) return f;
                }
                catch (Exception) { }
        }
        catch (Exception) { }
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe");
        return File.Exists(local) ? local : null;
    }

    static int RunClaude(string exe, string args)
    {
        if (NET_OFF) return -1;
        try
        {
            var psi = exe != null ? new ProcessStartInfo(exe, args) : new ProcessStartInfo("cmd.exe", "/c claude " + args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch (Exception) { } return -1; }
                return p.ExitCode;
            }
        }
        catch (Exception e) { Report("update: " + e.Message); return -1; }
    }

    void OpenRepo()
    {
        if (NET_OFF) return;
        try { Process.Start(new ProcessStartInfo(REPO) { UseShellExecute = true }); }
        catch (Exception e) { Report("repo: " + e.Message); }
    }

    // Человек сам нажал «звезда»: ставим её через GitHub CLI с его аккаунта; без gh или без входа откроем страницу.
    void StarRepo()
    {
        if (NET_OFF) return;
        if (!starAsked) { starAsked = true; SaveSettings(); }
        if (starring) return;
        starring = true;
        Notify(Tr("Ставлю звезду…", "Starring…"), null);
        new Thread(() => { starDone = GhStar() ? 1 : -1; }) { IsBackground = true, Name = "pet-star" }.Start();
    }

    static bool GhStar()
    {
        if (NET_OFF) return false;
        string gh = FindGh();
        if (gh == null) return false;
        try
        {
            var psi = new ProcessStartInfo(gh, "api --method PUT user/starred/Alexanderadon/edgerunners")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
            using (var p = Process.Start(psi))
            {
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(20000)) { try { p.Kill(); } catch (Exception) { } return false; }
                return p.ExitCode == 0;
            }
        }
        catch (Exception) { return false; }
    }

    // gh.exe из PATH или из обычных мест установки.
    static string FindGh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try
            {
                string d = dir.Trim().Trim('"');
                if (d.Length > 0 && File.Exists(Path.Combine(d, "gh.exe"))) return Path.Combine(d, "gh.exe");
            }
            catch (Exception) { }
        foreach (var f in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"GitHub CLI\gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\GitHub CLI\gh.exe") })
            if (File.Exists(f)) return f;
        return null;
    }

    void CheckNow()
    {
        if (NET_OFF) return;
        remoteVer = null;
        handledVer = "";
        checkReport = true;
        checkNow = true;
        Notify(Tr("Проверяю обновления…", "Checking for updates…"), null);
    }

    // ---- запуск: папка мода; state.json читает фоновый поток, окно живёт в углу экрана само по себе ----
    string root = "", statePath, snapRequest, menuRequest, pickRequest, pokeRequest, snapPath, monRequest;
    Thread loader, feed, beat, sampler;
    volatile bool closing;
    // Heartbeat для плагина: assets/pet/pet.beat {"pid":…,"at":<ms>} — обновляется раз в 5 с, удаляется при выходе.
    string beatPath;

    public void Start(string modRoot)
    {
        root = modRoot ?? "";
        statePath = Path.Combine(root, @"assets\pet\state.json");
        snapRequest = Path.Combine(root, @"assets\pet\snap.request");
        menuRequest = Path.Combine(root, @"assets\pet\menu.request");
        pickRequest = Path.Combine(root, @"assets\pet\pick.request");
        pokeRequest = Path.Combine(root, @"assets\pet\poke.request");
        recordRequest = Path.Combine(root, @"assets\pet\record.request");
        monRequest = Path.Combine(root, @"assets\pet\mon.request");
        snapPath = Path.Combine(root, @"preview\pet-live.png");
        ChoicePath = Path.Combine(root, @"assets\pet\choice.json");
        settingsPath = Path.Combine(root, @"assets\pet\settings.json");
        beatPath = Path.Combine(root, @"assets\pet\pet.beat");
        crashPath = Path.Combine(root, @"assets\pet\pet-crash.log");
        // Журнал не растёт бесконечно: старый — в архив.
        try
        {
            if (File.Exists(crashPath) && new FileInfo(crashPath).Length > 256 * 1024)
                File.Move(crashPath, crashPath + ".old");
        }
        catch (Exception) { }
        // Любое необработанное исключение UI-потока — в pet-crash.log, без диалога «ошибка PowerShell».
        // Сам режим (CatchException) выставляется в pet.ps1 ДО создания формы: после создания контролов
        // WinForms его менять запрещает.
        Application.ThreadException += (s, e) => LogCrash("ui-thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash("domain", e.ExceptionObject as Exception);
        LoadSettings();
        InitHome();
        if (string.IsNullOrEmpty(firstRun)) { firstRun = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); SaveSettings(); }
        localVer = ReadLocalVersion();
        new Thread(UpdateLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "pet-update" }.Start();
        feed = new Thread(FeedLoop) { IsBackground = true, Name = "pet-feed" };
        feed.Start();
        WriteBeat();
        beat = new Thread(BeatLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "pet-beat" };
        beat.Start();
        sampler = new Thread(SamplerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "pet-monitor" };
        sampler.Start();
        new Thread(PreJit) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "pet-jit" }.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        clock.Start();
        DrainInbox();
        frameTimer.Start();
        followTimer.Start();
        KeepOnTop();
        // Панель мониторинга — отдельное окно; при monitor=true показываем сразу на сохранённом месте.
        if (monitorOn) { try { ShowMonitor(); } catch (Exception ex) { LastError = "mon: " + ex.Message; } }
    }

    // ---- что играть и что говорить (от мода) ----
    // Эффекты шага: присел, вытянулся, подскок, дрожь, приземление.
    const int FX_HOP = 4, FX_SHAKE = 8;
    struct Step { public string F; public double Ms; public int Fx; }
    List<Step> timeline = new List<Step>(), loop = new List<Step>();
    DateTime started = DateTime.Now;
    long serial;
    string dir;
    string line = "";
    DateTime sayAt = DateTime.MinValue, sayUntil = DateTime.MinValue;

    // Имя героя для шапки карточки (Роман, ...); без него — имя папки кадров. Роль — тег в шапке (ОПЕРАТОР, ...).
    public string HeroName = "", HeroRole = "";
    // Кто сейчас на связи (поле hero из state.json) — по нему метка «АКТИВЕН» в меню.
    public string HeroKey = "";
    // Куда записать выбор из меню (assets/pet/choice.json).
    public string ChoicePath;
    // Герой включён (у «выкл» поле hero пустое — питомец прячется).
    bool heroOn;

    static int ParseFx(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int fx = 0;
        foreach (var w in s.ToLowerInvariant().Split(new[] { ' ', ',', '+', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
            switch (w)
            {
                case "hop": fx |= FX_HOP; break;
                case "shake": fx |= FX_SHAKE; break;
            }
        return fx;
    }

    public void SetHero(string folder)
    {
        if (string.IsNullOrEmpty(folder) || (cur != null && string.Equals(folder, cur.Folder, StringComparison.OrdinalIgnoreCase))) return;
        var set = OpenSet(folder);
        // Папка без кадров (герой ещё не нарисован) — остаёмся на прежних кадрах, а не пропадаем.
        if (set == null || !set.Names.Contains("idle_01")) return;
        cur = set;
        dir = folder;
        // Сначала стойка, потом кадры текущей цепочки и выхода героя, потом всё остальное — в фоне.
        Want(set, "idle_01", true);
        WantSteps(set, timeline);
        WantSteps(set, loop);
        WantSteps(set, over);
        WantSteps(set, enter);
        foreach (var n in set.Sorted) Want(set, n, false);
    }

    // Новая цепочка от мода.
    void SetChain(List<Step> tl, List<Step> lp)
    {
        // Та же петля уже крутится, а новая цепочка лишь снова входит в неё (тот же клип, подъём камеры) — не
        // перезапускаем: иначе вход в сцену повторялся бы на каждое событие.
        if (InLoop() && Same(lp, loop) && OnlyEntry(tl, lp)) return;
        // Мод прислал выход героя после выбора в меню, а поклон уже играет (или сыгран) — второй раз не кланяемся.
        double skip = 0;
        if ((DateTime.Now - pickedAt).TotalSeconds < 15 && enter.Count > 0 && tl.Count > 0 && tl[0].F == enter[0].F)
            for (int i = 0; i < tl.Count && i < enter.Count && tl[i].F == enter[i].F; i++) skip += tl[i].Ms;
        timeline = tl;
        loop = lp;
        serial++;
        if (cur != null) { WantSteps(cur, tl); WantSteps(cur, lp); }
        // Выход героя и ответ на клик доигрывают до конца, затем — новая цепочка.
        if (over != null) { overResume = skip; return; }
        started = DateTime.Now.AddMilliseconds(-skip);
    }

    bool InLoop()
    {
        // Пока идёт жест поверх сцены, часы цепочки стоят на том месте, куда она вернётся.
        double c = over != null ? overResume : (DateTime.Now - started).TotalMilliseconds;
        foreach (var s in timeline) c -= s.Ms;
        return c >= 0 && loop.Count > 0;
    }

    static bool Same(List<Step> a, List<Step> b)
    {
        if (a.Count != b.Count || a.Count == 0) return false;
        for (int i = 0; i < a.Count; i++) if (a[i].F != b[i].F || a[i].Ms != b[i].Ms) return false;
        return true;
    }

    // Цепочка только входит в петлю: её кадры — кадры петли и переходы к ним (подъём камеры, рука к подбородку).
    static readonly HashSet<string> ENTRY = new HashSet<string> { "raisecam", "tochin", "rc", "tc" };
    static bool OnlyEntry(List<Step> tl, List<Step> lp)
    {
        var clips = new HashSet<string>();
        foreach (var s in lp) clips.Add(Clip(Base(s.F)));
        foreach (var s in tl) { string c = Clip(Base(s.F)); if (!clips.Contains(c) && !ENTRY.Contains(c)) return false; }
        return true;
    }

    // Реплики не мелькают: новая сменит видимую не раньше чем через MIN_LINE_MS (последняя ждёт своей очереди);
    // urgent — ответ на клик и выбор героя, сразу.
    const double MIN_LINE_MS = 4000, TEXT_FADE_MS = 220, CARD_IN_MS = 240, CARD_OUT_MS = 340;
    string pendingLine;
    DateTime lineAt = DateTime.MinValue;
    const double RARE_GAP_S = 45;

    public void Say(string text, bool urgent = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var t = DateTime.Now;
        // Карточка об обновлении или звезде держится; реплика покажется после неё.
        if (t < noticeUntil && text != line) { pendingLine = text; return; }
        bool visible = t < sayUntil;
        if (!urgent && (linesMode == 2 || (linesMode == 1 && (visible || (t - sayAt).TotalSeconds < RARE_GAP_S)))) return;
        if (visible && text == line) { pendingLine = null; sayUntil = t.AddSeconds(8); return; }
        if (visible && !urgent && (t - lineAt).TotalMilliseconds < MIN_LINE_MS) { pendingLine = text; return; }
        // Клик отвечает сразу, а реплика о деле ждёт своей очереди и потом всё равно покажется.
        if (!urgent) pendingLine = null;
        if (!visible) sayAt = t;
        line = text;
        lineAt = t;
        sayUntil = t.AddSeconds(8);
    }

    public void Repeat() { Say(line); }

    // ---- свои короткие цепочки поверх цепочки мода: реакция на клик и выход героя ----
    // Цепочка мода на это время замирает и потом идёт дальше с того же места (или со своей петли).
    List<Step> over;
    DateTime overStart;
    double overResume;
    bool overEnter;
    readonly List<List<Step>> pokes = new List<List<Step>>();
    string[] pokeLines = new string[0], pokeMad = new string[0];
    List<Step> enter = new List<Step>();
    int lastPoke = -1, lastPokeLine = -1, lastPokeMad = -1;
    // Что сыграть сразу за текущим жестом (злость на серию кликов) и шла ли съёмка: напарник в кадре дождётся конца жеста.
    List<Step> overNext;
    bool overOnFilm;

    void StartOverlay(List<Step> steps, bool isEnter)
    {
        if (steps == null || steps.Count == 0) return;
        if (over == null) overResume = (DateTime.Now - started).TotalMilliseconds;
        over = steps;
        overNext = null;
        overStart = DateTime.Now;
        overEnter = isEnter;
        wanderTarget = double.NaN;
        serial++;
    }

    // Случайный номер без повтора подряд.
    int Pick(int n, ref int last)
    {
        int i = rnd.Next(n);
        if (n > 1 && i == last) i = (last + 1 + rnd.Next(n - 1)) % n;
        last = i;
        return i;
    }

    // ЛКМ по герою. Отклик есть всегда и сразу, что бы он ни делал: всплеск в точке клика, вздрагивание, реплика.
    // Жест играет поверх сцены, и потом сцена идёт дальше с того же места; начатый жест клик не обрывает.
    // Серия кликов — герой злится. false — реакций нет (мод ещё не прислал).
    const int MAD_CLICKS = 4, HIT_PAD = 6;
    static readonly SolidBrush HIT_AREA = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
    const double MAD_WINDOW_S = 3, POKE_LINE_GAP_MS = 1200;
    readonly Queue<DateTime> clickTimes = new Queue<DateTime>();
    DateTime pokeSaidAt = DateTime.MinValue, shakeUntil = DateTime.MinValue;

    public bool Poke(Point? at = null)
    {
        if (menuOpen) return true;
        if (at.HasValue) AddTap(at.Value);
        if (NoticeClick()) return true;
        if (pokes.Count == 0) return false;
        var t = DateTime.Now;
        clickTimes.Enqueue(t);
        while ((t - clickTimes.Peek()).TotalSeconds > MAD_WINDOW_S) clickTimes.Dequeue();
        bool mad = clickTimes.Count >= MAD_CLICKS;
        if (mad) clickTimes.Clear();

        long key;
        string clip = Clip(Base(Current(out key).F));
        Flinch(clip);
        var steps = PokeFor(clip, mad);
        if (steps != null)
        {
            if (over == null) { overOnFilm = clip == "film" || clip == "an"; StartOverlay(steps, false); }
            else if (mad && !overEnter) overNext = steps;
        }
        PokeLine(mad, t);
        return true;
    }

    // Вздрагивание видно мгновенно: стоя — подскок, сидя — поменьше, лёжа — только дрожь.
    void Flinch(string clip)
    {
        bool lying = clip == "lie" || clip == "sleep" || clip == "liedown" || clip == "fallasleep" || clip == "ld" || clip == "fa";
        if (lying) shakeUntil = DateTime.Now.AddMilliseconds(220);
        else if (hopY > -1) hopV = LOW.Contains(clip) ? -130 : -230;
    }

    // Что ответить: на серию — злость; пока Claude работает, иногда о деле (при «только по клику» — всегда о деле).
    void PokeLine(bool mad, DateTime t)
    {
        if (!mad && (t - pokeSaidAt).TotalMilliseconds < POKE_LINE_GAP_MS) return;
        pokeSaidAt = t;
        if (mad && pokeMad.Length > 0) Say(pokeMad[Pick(pokeMad.Length, ref lastPokeMad)], true);
        else if (holdBusy && !string.IsNullOrEmpty(lastLine) && (linesMode == 2 || rnd.Next(2) == 0)) Say(lastLine, true);
        else if (pokeLines.Length > 0) Say(pokeLines[Pick(pokeLines.Length, ref lastPokeLine)], true);
    }

    // Жест по позе: стоя — из любой сцены, через стойку, если герой занят; сидя и лёжа отвечает, не вскакивая;
    // на переходе (садится, ложится) поза не меняется.
    static readonly HashSet<string> POKE_READY = new HashSet<string> { "idle", "iv", "look", "filmyou", "hello" };
    static Step S(string f, double ms) { return new Step { F = f, Ms = ms }; }
    List<Step> PokeFor(string clip, bool mad)
    {
        if (clip == "sit" || clip == "yawn" || clip == "doze") return new List<Step> { S("sit_02", 140), S("sit_01", 320), S("sit_02", 140) };
        if (clip == "lie") return new List<Step> { S("lie_02", 160), S("lie_01", 360), S("lie_02", 160) };
        if (clip == "sleep") return new List<Step> { S("fa_03", 200), S("fa_02", 200), S("fa_01", 900), S("fa_02", 220), S("fa_03", 220) };
        if (LOW.Contains(clip)) return null;
        var steps = new List<Step>();
        if (!POKE_READY.Contains(clip)) steps.Add(S("idle_01", 90));
        if (mad) steps.AddRange(new[] { S("angry_01", 240), S("angry_02", 180), S("angry_01", 150), S("angry_02", 180), S("angry_03", 700), S("angry_04", 420) });
        else steps.AddRange(pokes[Pick(pokes.Count, ref lastPoke)]);
        return steps;
    }

    // Всплеск в точке клика: кольцо и искры. Это ответ интерфейса, поэтому виден и при выключенных эффектах.
    sealed class Tap { public float X, Y; public double Age; public double[] Ang, Dist; }
    readonly List<Tap> taps = new List<Tap>();
    const double TAP_LIFE = 0.42;
    const int TAP_SPARKS = 7;

    void AddTap(Point at)
    {
        if (taps.Count >= 6) taps.RemoveAt(0);
        var t = new Tap { X = at.X + fxLeft, Y = at.Y, Ang = new double[TAP_SPARKS], Dist = new double[TAP_SPARKS] };
        double start = rnd.NextDouble() * Math.PI * 2;
        for (int i = 0; i < TAP_SPARKS; i++)
        {
            t.Ang[i] = start + i * Math.PI * 2 / TAP_SPARKS + (rnd.NextDouble() - 0.5) * 0.5;
            t.Dist[i] = 30 + rnd.NextDouble() * 26;
        }
        taps.Add(t);
    }

    void DrawTaps(double dt)
    {
        for (int i = taps.Count - 1; i >= 0; i--)
        {
            var t = taps[i];
            t.Age += dt;
            if (t.Age >= TAP_LIFE) { taps.RemoveAt(i); continue; }
            double k = t.Age / TAP_LIFE, ease = 1 - (1 - k) * (1 - k), a = 1 - k;
            float x0 = t.X - fxLeft, y0 = t.Y, r = (float)(6 + 24 * ease);
            using (var pen = new Pen(Al(CYAN, a * 0.85), (float)(0.8 + 2 * a))) g.DrawEllipse(pen, x0 - r, y0 - r, r * 2, r * 2);
            for (int j = 0; j < TAP_SPARKS; j++)
                Spark(x0 + (float)(Math.Cos(t.Ang[j]) * t.Dist[j] * ease), y0 + (float)(Math.Sin(t.Ang[j]) * t.Dist[j] * ease), (float)(1 + 1.8 * a), YELLOW, a);
        }
    }

    // ЛКМ: окно DeepSeek Harness вперёд (если оно есть) и реакция героя; без реакций — повторить реплику.
    void Clicked(Point at)
    {
        IntPtr h = FindClaude();
        if (h != IntPtr.Zero) SetForegroundWindow(h);
        if (PetClicked != null) PetClicked(this, EventArgs.Empty);
        if (!Poke(at)) Repeat();
    }

    // Текущий шаг и его номер: эффект срабатывает раз на шаг, даже если кадр повторяется.
    Step Current(out long key)
    {
        if (over != null)
        {
            double t = (DateTime.Now - overStart).TotalMilliseconds;
            for (int i = 0; i < over.Count; i++)
            {
                if (t < over[i].Ms) { key = (serial << 32) + i; return over[i]; }
                t -= over[i].Ms;
            }
            if (overNext != null)
            {
                over = overNext;
                overNext = null;
                overStart = DateTime.Now;
                serial++;
                key = serial << 32;
                return over[0];
            }
            started = DateTime.Now.AddMilliseconds(-overResume);
            over = null;
            serial++;
        }
        double c = (DateTime.Now - started).TotalMilliseconds;
        for (int i = 0; i < timeline.Count; i++)
        {
            if (c < timeline[i].Ms) { key = (serial << 32) + i; return timeline[i]; }
            c -= timeline[i].Ms;
        }
        double total = 0;
        foreach (var s in loop) total += s.Ms;
        if (total > 0)
        {
            long cycle = (long)(c / total);
            c -= cycle * total;
            for (int j = 0; j < loop.Count; j++)
            {
                if (c < loop[j].Ms) { key = (serial << 32) + timeline.Count + (cycle % (1 << 20)) * loop.Count + j; return loop[j]; }
                c -= loop[j].Ms;
            }
        }
        key = (serial << 32) + 0xFFFFFFFFL;
        return new Step { F = "idle_01", Ms = 1000 };
    }

    // ---- напарники для меню (от мода) ----
    class Hero
    {
        public string Id, Sprite, Folder, Name, Role, About;
        public string[] Labels = new string[0];
        public double[] Values = new double[0];
        // Кадры героя (бюст для карточки — из его idle_01, грузится в фоне при открытии меню).
        public FrameSet Set;
        public bool SetTried;
        // Закрытый слот в меню: герой придёт с обновлением.
        public bool Locked;
        public Hero Copy() { return new Hero { Id = Id, Sprite = Sprite, Folder = Folder, Name = Name, Role = Role, About = About, Labels = Labels, Values = Values }; }
    }
    readonly List<Hero> roster = new List<Hero>();
    string rosterKey;

    // Только что выбран герой в меню: мод может ещё прислать прежнего, пока не прочтёт choice.json.
    // Прежнего не слушаем, пока мод не пришлёт выбранного (или PICK_HOLD секунд) — выбор важнее.
    string pickedKey;
    DateTime pickedAt = DateTime.MinValue;
    bool StaleHero(string hero)
    {
        if (pickedKey == null) return false;
        if (hero == pickedKey || (DateTime.Now - pickedAt).TotalSeconds > PICK_HOLD) { pickedKey = null; return false; }
        return true;
    }

    // ---- кадры: грузятся и меряются в фоне, окно берёт готовые ----
    // Кадр, готовый к показу: картинка обрезана по фигуре с запасом; рамки — в координатах полного кадра.
    sealed class FrameData
    {
        public FrameSet Owner;
        public Bitmap Img;
        public int OX, OY, FW, FH;
        // Фигура (альфа больше половины), ступни (нижняя четверть кадра).
        public Rectangle Box, Feet;
        // Середина головы по x (плотные пиксели у макушки) и середина верхней трети фигуры (для бюста).
        public float HeadX, UpperX;
        // Кадр в размере героя из настроек (при 100% не нужен).
        public ScaledFrame Big;
    }

    sealed class ScaledFrame { public Bitmap Img; public int X, Y; public int Pct; }

    // Папка кадров одного героя: имена кадров, число кадров клипов, готовые кадры, бюст для меню.
    sealed class FrameSet
    {
        public string Folder;
        public readonly HashSet<string> Names = new HashSet<string>();
        public readonly List<string> Sorted = new List<string>();
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        // Готовые кадры — под lock(Ready): их кладёт фоновый поток.
        public readonly Dictionary<string, FrameData> Ready = new Dictionary<string, FrameData>();
        // В очереди и не загрузившиеся — под jobLock.
        public readonly HashSet<string> Queued = new HashSet<string>();
        public readonly Dictionary<string, DateTime> Failed = new Dictionary<string, DateTime>();
        public volatile Bitmap Bust;
        public volatile bool Dead;
        public DateTime Used = DateTime.Now, Scanned = DateTime.Now;
        public FrameData Get(string n) { lock (Ready) { FrameData d; return Ready.TryGetValue(n, out d) ? d : null; } }
        public int ReadyCount { get { lock (Ready) return Ready.Count; } }
    }

    struct Job { public FrameSet Set; public string Name; }
    readonly Dictionary<string, FrameSet> sets = new Dictionary<string, FrameSet>(StringComparer.OrdinalIgnoreCase);
    readonly object jobLock = new object();
    readonly LinkedList<Job> jobs = new LinkedList<Job>();
    // Кадры текущего героя и последний показанный кадр (пока нужный грузится — рисуем его).
    FrameSet cur;
    FrameData shown;
    volatile string loadError = "";

    // Папка героя: список кадров (одно чтение каталога), число кадров клипов. Кадров нет — null.
    FrameSet OpenSet(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        FrameSet set;
        if (sets.TryGetValue(folder, out set)) { set.Used = DateTime.Now; return set; }
        set = new FrameSet { Folder = folder };
        if (!Scan(set)) return null;
        sets[folder] = set;
        return set;
    }

    static bool Scan(FrameSet set)
    {
        string[] files;
        try { files = Directory.GetFiles(set.Folder, "*.png"); }
        catch (Exception) { return false; }
        set.Names.Clear();
        set.Sorted.Clear();
        set.Counts.Clear();
        foreach (var p in files)
        {
            string n = Path.GetFileNameWithoutExtension(p);
            // Кадры вдоха «…b» не нужны: дыхание плавное, берётся основной кадр.
            if (Base(n) != n) continue;
            set.Names.Add(n);
        }
        set.Sorted.AddRange(set.Names);
        set.Sorted.Sort(StringComparer.Ordinal);
        // Клип: clip_01, clip_02, … подряд.
        foreach (var n in set.Sorted)
        {
            string c = Clip(n);
            if (set.Counts.ContainsKey(c)) continue;
            int k = 0;
            while (k < 24 && set.Names.Contains(c + "_" + (k + 1).ToString("00"))) k++;
            set.Counts[c] = k;
        }
        set.Scanned = DateTime.Now;
        return set.Names.Count > 0;
    }

    // Поставить кадр в очередь фоновой загрузки; срочный — в начало.
    void Want(FrameSet set, string name, bool urgent)
    {
        if (set == null || name == null) return;
        name = Base(name);
        if (!set.Names.Contains(name) || set.Get(name) != null) return;
        lock (jobLock)
        {
            DateTime failedAt;
            if (set.Failed.TryGetValue(name, out failedAt) && (DateTime.Now - failedAt).TotalSeconds < 3) return;
            if (set.Queued.Contains(name))
            {
                if (!urgent || (jobs.First != null && jobs.First.Value.Set == set && jobs.First.Value.Name == name)) return;
                for (var node = jobs.First; node != null; node = node.Next)
                    if (node.Value.Set == set && node.Value.Name == name) { jobs.Remove(node); jobs.AddFirst(node); break; }
                return;
            }
            set.Queued.Add(name);
            var job = new Job { Set = set, Name = name };
            if (urgent) jobs.AddFirst(job); else jobs.AddLast(job);
            Monitor.Pulse(jobLock);
        }
    }

    void WantSteps(FrameSet set, List<Step> steps)
    {
        if (steps != null) foreach (var s in steps) Want(set, s.F, false);
    }

    // Фоновая загрузка: декод PNG, обрезка по фигуре, рамки — всё через LockBits, вне UI-потока.
    // Буфер пикселей один на поток: иначе каждый кадр — 0,4 МБ в куче больших объектов и полные сборки мусора,
    // которые останавливают и UI-поток.
    void LoaderLoop()
    {
        byte[] buf = null;
        while (true)
        {
            Job job;
            lock (jobLock)
            {
                while (jobs.Count == 0 && !closing) Monitor.Wait(jobLock, 500);
                if (closing) return;
                job = jobs.First.Value;
                jobs.RemoveFirst();
                job.Set.Queued.Remove(job.Name);
            }
            if (job.Set.Dead) continue;
            FrameData d;
            try { d = Decode(Path.Combine(job.Set.Folder, job.Name + ".png"), ref buf); }
            catch (Exception e)
            {
                loadError = job.Name + ": " + e.Message;
                lock (jobLock) job.Set.Failed[job.Name] = DateTime.Now;
                continue;
            }
            d.Owner = job.Set;
            int pct = sizePct;
            if (pct != 100) try { d.Big = ScaleFrame(d, pct); } catch (Exception) { }
            Bitmap bust = null;
            if (job.Name == "idle_01" && job.Set.Bust == null)
                try { bust = MakeBust(d, CARD_W - 20, PORT_H); } catch (Exception) { }
            bool keep;
            lock (job.Set.Ready)
            {
                keep = !job.Set.Dead;
                if (keep)
                {
                    job.Set.Ready[job.Name] = d;
                    if (bust != null) job.Set.Bust = bust;
                }
            }
            if (!keep) { d.Img.Dispose(); if (d.Big != null) d.Big.Img.Dispose(); if (bust != null) bust.Dispose(); }
        }
    }

    // Методы движка компилируются заранее, в фоне: первый показ эффекта или меню не ждёт JIT в UI-потоке.
    static void PreJit()
    {
        try
        {
            var types = new List<Type> { typeof(PetForm) };
            types.AddRange(typeof(PetForm).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var m in t.GetMethods(all))
                    if (!m.IsAbstract && !m.ContainsGenericParameters)
                        try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); } catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    static FrameData Decode(string path, ref byte[] px)
    {
        // Файл открыт на чтение с общим доступом и закрывается сразу после декода — кадры можно пересобирать.
        int w, h;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var src = new Bitmap(fs))
        {
            w = src.Width;
            h = src.Height;
            if (px == null || px.Length < w * h * 4) px = new byte[w * h * 4];
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4); }
            finally { src.UnlockBits(data); }
        }
        // Один проход по альфе: границы непрозрачного (обрезка) и по строкам — плотные пиксели (фигура).
        var rmin = new int[h];
        var rmax = new int[h];
        var rsum = new double[h];
        var rcnt = new int[h];
        int cl = w, cr = -1, ct = -1, cb = -1;
        for (int y = 0; y < h; y++)
        {
            rmin[y] = int.MaxValue;
            rmax[y] = -1;
            for (int x = 0, o = y * w * 4 + 3; x < w; x++, o += 4)
            {
                byte a = px[o];
                if (a == 0) continue;
                if (x < cl) cl = x;
                if (x > cr) cr = x;
                if (ct < 0) ct = y;
                cb = y;
                if (a > 128)
                {
                    if (x < rmin[y]) rmin[y] = x;
                    rmax[y] = x;
                    rsum[y] += x;
                    rcnt[y]++;
                }
            }
        }
        var d = new FrameData { FW = w, FH = h };
        d.Box = Band(rmin, rmax, 0, h);
        if (d.Box.Width <= 0) d.Box = new Rectangle(0, FRAME_G, 1, 1);
        d.Feet = Band(rmin, rmax, h * 3 / 4, h);
        int top = d.Box.Top;
        var upper = Band(rmin, rmax, top, top + (int)(d.Box.Height * 0.3f));
        d.UpperX = upper.Width > 0 ? upper.X + upper.Width / 2f : d.Box.X + d.Box.Width / 2f;
        double sum = 0;
        int n = 0;
        for (int y = Math.Max(0, top); y < Math.Min(h, top + 36); y++) { sum += rsum[y]; n += rcnt[y]; }
        d.HeadX = n > 0 ? (float)(sum / n) : d.Box.X + d.Box.Width / 2f;
        // Обрезка по фигуре с запасом в 2 пикселя: меньше памяти и быстрее рисовать.
        if (cr < 0) { cl = 0; cr = 0; ct = 0; cb = 0; }
        int ox = Math.Max(0, cl - 2), oy = Math.Max(0, ct - 2);
        int iw = Math.Min(w, cr + 3) - ox, ih = Math.Min(h, cb + 3) - oy;
        var img = new Bitmap(iw, ih, PixelFormat.Format32bppPArgb);
        var wd = img.LockBits(new Rectangle(0, 0, iw, ih), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { for (int y = 0; y < ih; y++) Marshal.Copy(px, ((oy + y) * w + ox) * 4, wd.Scan0 + y * wd.Stride, iw * 4); }
        finally { img.UnlockBits(wd); }
        d.Img = img;
        d.OX = ox;
        d.OY = oy;
        return d;
    }

    // Кадр в размере героя: пересчитывается один раз, дальше ложится на холст точка в точку, поэтому контур не рябит.
    // Сетка общая для всех кадров (координаты кадра × масштаб), поэтому соседние кадры не расходятся на пиксель.
    static ScaledFrame ScaleFrame(FrameData d, int pct)
    {
        double k = pct / 100.0;
        int iw = d.Img.Width, ih = d.Img.Height;
        int bx = (int)Math.Floor(d.OX * k), by = (int)Math.Floor(d.OY * k);
        int bw = (int)Math.Ceiling((d.OX + iw) * k) - bx + 1, bh = (int)Math.Ceiling((d.OY + ih) * k) - by + 1;
        var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
        using (var gr = Graphics.FromImage(bmp))
        using (var ia = new ImageAttributes())
        {
            gr.CompositingMode = CompositingMode.SourceCopy;
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Без этого края кадра при пересчёте смешиваются с пустотой и темнеют.
            ia.SetWrapMode(WrapMode.TileFlipXY);
            float x0 = (float)(d.OX * k - bx), y0 = (float)(d.OY * k - by), w = (float)(iw * k), h = (float)(ih * k);
            gr.DrawImage(d.Img, new[] { new PointF(x0, y0), new PointF(x0 + w, y0), new PointF(x0, y0 + h) }, new RectangleF(0, 0, iw, ih), GraphicsUnit.Pixel, ia);
        }
        return new ScaledFrame { Img = bmp, X = bx, Y = by, Pct = pct };
    }

    // Кадр нужного размера. Обычно он уже готов (загрузчик или шаг пересчёта); если нет — пересчёт сразу. Только в UI-потоке.
    ScaledFrame BigOf(FrameData d)
    {
        var big = d.Big;
        if (big != null && big.Pct == sizePct) return big;
        d.Big = ScaleFrame(d, sizePct);
        if (big != null) big.Img.Dispose();
        return d.Big;
    }

    // После смены размера готовые кадры героя пересчитываются по одному за кадр анимации, чтобы не было рывка.
    readonly Queue<FrameData> rescale = new Queue<FrameData>();

    void ApplySize()
    {
        rescale.Clear();
        if (cur != null && sizePct != 100)
            lock (cur.Ready) foreach (var d in cur.Ready.Values) rescale.Enqueue(d);
    }

    // Рамка плотных пикселей в полосе строк [y0, y1).
    static Rectangle Band(int[] rmin, int[] rmax, int y0, int y1)
    {
        y0 = Math.Max(0, y0);
        y1 = Math.Min(rmin.Length, y1);
        int l = int.MaxValue, r = -1, t = -1, b = -1;
        for (int y = y0; y < y1; y++)
        {
            if (rmax[y] < 0) continue;
            if (t < 0) t = y;
            b = y;
            if (rmin[y] < l) l = rmin[y];
            if (rmax[y] > r) r = rmax[y];
        }
        return r < 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, b + 1);
    }

    // Часть кадра (в координатах полного кадра) в прямоугольник dst — с учётом обрезки картинки.
    static void DrawPart(Graphics gr, FrameData d, RectangleF src, RectangleF dst, ImageAttributes ia)
    {
        var crop = new RectangleF(d.OX, d.OY, d.Img.Width, d.Img.Height);
        var s = RectangleF.Intersect(src, crop);
        if (s.Width <= 0 || s.Height <= 0) return;
        float kx = dst.Width / src.Width, ky = dst.Height / src.Height;
        float dx = dst.X + (s.X - src.X) * kx, dy = dst.Y + (s.Y - src.Y) * ky, dw = s.Width * kx, dh = s.Height * ky;
        s.Offset(-d.OX, -d.OY);
        if (ia == null) gr.DrawImage(d.Img, new RectangleF(dx, dy, dw, dh), s, GraphicsUnit.Pixel);
        else gr.DrawImage(d.Img, new[] { new PointF(dx, dy), new PointF(dx + dw, dy), new PointF(dx, dy + dh) }, s, GraphicsUnit.Pixel, ia);
    }

    // Бюст для карточки меню: голова и плечи из idle_01. Центр — по верху фигуры: рука с камерой тянет рамку вбок.
    static Bitmap MakeBust(FrameData d, int w, int h)
    {
        var box = d.Box;
        if (box.Width <= 1) return null;
        float ch = box.Height * 0.54f, cwid = ch * w / h;
        var src = new RectangleF(d.UpperX - cwid / 2, box.Top - box.Height * 0.04f, cwid, ch);
        var bust = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var bg = Graphics.FromImage(bust))
        {
            bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawPart(bg, d, src, new RectangleF(0, 0, w, h), null);
        }
        return bust;
    }

    // Похожие клипы: у героя нет кадров клипа — играем кадр того же номера из похожего (ракурсы съёмки — та же съёмка).
    static readonly Dictionary<string, string> ALIAS = new Dictionary<string, string> { { "an", "film" } };

    // Какой кадр героя будет на экране: свой, похожий (ALIAS) или стойка.
    string Resolve(string name)
    {
        var set = cur;
        if (set == null || set.Names.Contains(name)) return name;
        string alias;
        if (ALIAS.TryGetValue(Clip(name), out alias))
        {
            int n = FrameNo(name), count;
            if (n > 0 && set.Counts.TryGetValue(alias, out count) && count > 0)
            {
                string a = alias + "_" + ((n - 1) % count + 1).ToString("00");
                if (set.Names.Contains(a)) return a;
            }
        }
        return "idle_01";
    }

    // Кадр текущего героя; нет такого — похожий или стойка; ещё грузится — прежний кадр (UI-поток не ждёт диска).
    FrameData Frame(string name)
    {
        var set = cur;
        if (set == null) return shown;
        name = Resolve(name);
        var d = set.Get(name);
        if (d != null) return d;
        Want(set, name, true);
        return shown ?? set.Get("idle_01");
    }

    // Сколько кадров у клипа в папке героя (clip_01, clip_02, … подряд).
    int Frames(string clip)
    {
        int n;
        return cur != null && cur.Counts.TryGetValue(clip, out n) ? n : 0;
    }

    // Скорость походки: длина шага — по кадру касания (ноги шире всего), от пятки передней ноги до носка задней
    // без длины стопы; за полцикла тело проходит один шаг.
    readonly Dictionary<string, double> speeds = new Dictionary<string, double>();
    double Speed(string gait, double ms)
    {
        double v;
        string key = cur.Folder + "|" + gait;
        if (speeds.TryGetValue(key, out v)) return v;
        var b = cur.Get(gait + "_01");
        double half = Math.Max(2, Frames(gait)) / 2 * ms / 1000.0;
        // Кадр касания ещё грузится — примерная скорость, без запоминания.
        if (b == null) { Want(cur, gait + "_01", true); return 100 / half; }
        double stride = b.Feet.Width > 0 ? b.Feet.Width * 0.74 : 100;
        v = stride / half;
        speeds[key] = v;
        return v;
    }

    // Наборы кадров, которые давно не нужны (не текущий герой, не напарник, меню закрыто), — освобождаем.
    void Evict()
    {
        if (MenuShown) return;
        List<FrameSet> drop = null;
        foreach (var s in sets.Values)
        {
            bool busy = s == cur || s == coSet || (shown != null && shown.Owner == s) || (coShown != null && coShown.Owner == s);
            if (busy) { s.Used = DateTime.Now; continue; }
            if ((DateTime.Now - s.Used).TotalSeconds > 30)
            {
                if (drop == null) drop = new List<FrameSet>();
                drop.Add(s);
            }
        }
        if (drop != null) foreach (var s in drop) DropSet(s);
    }

    void DropSet(FrameSet set)
    {
        lock (set.Ready)
        {
            set.Dead = true;
            foreach (var d in set.Ready.Values) { d.Img.Dispose(); if (d.Big != null) d.Big.Img.Dispose(); }
            set.Ready.Clear();
            if (set.Bust != null) { set.Bust.Dispose(); set.Bust = null; }
        }
        sets.Remove(set.Folder);
    }

    // ---- где можно ходить: x ног в экранных координатах и линия пола ----
    int minX, maxX, groundY, scrLeft;
    int boxL = int.MinValue, boxR = int.MaxValue;
    bool hasArea;
    double x = double.NaN;
    int facing = -1;

    public void SetArea(int left, int right, int ground) { SetArea(left, right, ground, int.MinValue, int.MaxValue); }

    // windowLeft/windowRight — края окна DeepSeek Harness: меню и напарник не вылезают за них (и за экран).
    public void SetArea(int left, int right, int ground, int windowLeft, int windowRight)
    {
        minX = Math.Min(left, right);
        maxX = Math.Max(left, right);
        groundY = ground;
        boxL = windowLeft;
        boxR = windowRight;
        scrLeft = Screen.FromPoint(new Point(maxX, ground)).WorkingArea.Left;
        hasArea = true;
        if (double.IsNaN(x)) x = maxX;
    }

    // Кадры вдоха «…b» были ступенькой в 1–2 пикселя; теперь дыхание плавное, поэтому берём основной кадр.
    static string Base(string f)
    {
        int n = f.Length;
        return n > 3 && f[n - 1] == 'b' && char.IsDigit(f[n - 2]) ? f.Substring(0, n - 1) : f;
    }

    static string Clip(string f) { int i = f.IndexOf('_'); return i < 0 ? f : f.Substring(0, i); }

    static int FrameNo(string f)
    {
        int i = f.LastIndexOf('_'), n;
        return i >= 0 && int.TryParse(f.Substring(i + 1), out n) ? n : 0;
    }

    static readonly HashSet<string> LOW = new HashSet<string> { "sit", "yawn", "doze", "lie", "sleep", "liedown", "fallasleep", "sd", "ld", "fa" };
    // Спокойные клипы: от них можно отойти прогуляться.
    static readonly HashSet<string> CALM = new HashSet<string> { "idle", "iv", "hello", "look", "filmyou", "bored", "music", "phone" };

    // ---- связь с модом: state.json и окно DeepSeek Harness читает фоновый поток, окно получает готовое ----
    // Состояние от мода, уже разобранное.
    sealed class StateMsg
    {
        public string Seq, Hero, Name, Role, Line, Session;
        public bool Busy, Late;
        public List<Step> Timeline, Loop, Enter;
        public readonly List<List<Step>> Pokes = new List<List<Step>>();
        public string[] PokeLines = new string[0], PokeMad = new string[0];
        public readonly List<Hero> Roster = new List<Hero>();
        public string RosterKey = "";
        public Costar Costar;
        // Число открытых/занятых чатов из state.json (поля chats / busyChats); -1 — поля нет.
        public int Chats = -1, BusyChats = -1;
        // SPEND: траты за сегодня в рублях и дневной бюджет для шкалы; -1 — spendRub нет в state.json.
        public double SpendRub = -1, SpendBudgetRub = 100;
        public string SpendCurrency = "";
        // BALANCE: остаток на балансе в рублях; -1 — balanceRub нет в state.json.
        public double BalanceRub = -1;
        public string BalanceCurrency = "";
    }

    readonly List<StateMsg> inbox = new List<StateMsg>();
    long aliveTicks = DateTime.Now.Ticks;
    volatile string feedError = "";
    // Сессия, за которую держится питомец (только в потоке чтения).
    string holdSession;
    DateTime holdSeen;
    bool holdBusy;
    StateMsg parked;
    DateTime parkedAt;

    // Крупные кадры питомца, если есть; иначе обычные кадры мода. Полный путь — как есть (для проверок).
    string HeroDir(string sprite)
    {
        if (string.IsNullOrEmpty(sprite)) return "";
        try
        {
            if (Path.IsPathRooted(sprite)) return sprite;
            string d = Path.Combine(root, @"assets\pet\" + sprite);
            return Directory.Exists(d) ? d : Path.Combine(root, @"assets\" + sprite);
        }
        catch (Exception) { return ""; }
    }

    // Поток чтения: раз в 100 мс — изменился ли state.json (разбор тут же). Окно Harness тут больше не ищем:
    // позиция питомца считается только от экрана (см. Follow).
    void FeedLoop()
    {
        DateTime stamp = DateTime.MinValue;
        long size = -1;
        while (!closing)
        {
            try
            {
                var info = new FileInfo(statePath);
                if (info.Exists && (info.LastWriteTimeUtc != stamp || info.Length != size))
                {
                    StateMsg m = null;
                    try { m = ParseState(ReadShared(statePath)); } catch (Exception) { }
                    // Файл дописывается — разберём на следующем круге.
                    if (m != null)
                    {
                        stamp = info.LastWriteTimeUtc;
                        size = info.Length;
                        Interlocked.Exchange(ref aliveTicks, DateTime.Now.Ticks);
                        // Число чатов для HUD читает UI-поток — обновляем атомарно здесь.
                        Interlocked.Exchange(ref chatsOpen, m.Chats);
                        Interlocked.Exchange(ref chatsBusy, m.BusyChats);
                        if (Hold(m)) Post(m);
                    }
                }
                Unpark();
            }
            catch (Exception e) { feedError = "state: " + e.Message; }
            Thread.Sleep(100);
        }
    }

    static string ReadShared(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            return sr.ReadToEnd();
    }

    // Главное окно DeepSeek Harness. Процессов с этим именем несколько (main, renderer, GPU): main-окно
    // принадлежит только одному, у остальных MainWindowHandle пуст — поэтому берём первое непустое окно.
    static IntPtr FindClaude()
    {
        foreach (var p in Process.GetProcessesByName("DeepSeek Harness"))
        {
            try
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero && IsWindow(h)) return h;
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    void Post(StateMsg m) { lock (inbox) inbox.Add(m); }

    // Одна сессия за раз: несколько открытых сессий Claude пишут в один state.json. Питомец держится за свою;
    // чужое состояние берёт, только если та сессия работает, а своя нет, или своя молчит дольше HOLD_SILENCE.
    // Без поля session (старый мод) — берёт всё, как раньше. Чужое, пока держимся за свою, — мимо целиком.
    bool Hold(StateMsg m)
    {
        if (string.IsNullOrEmpty(m.Session)) return true;
        var now = DateTime.Now;
        // Демо (tools/pet-demo.mjs, session «demo») берётся всегда — проверка не ждёт, пока сессия освободится.
        if (holdSession == null || m.Session == "demo" || m.Session == holdSession || (m.Busy && !holdBusy) || (now - holdSeen).TotalSeconds > HOLD_SILENCE)
        {
            if (m.Session != holdSession) Report("session: hold " + m.Session + (m.Busy ? " (busy)" : ""));
            holdSession = m.Session;
            holdSeen = now;
            holdBusy = m.Busy;
            parked = null;
            return true;
        }
        if (parked == null || parked.Session != m.Session) Report("session: ignore " + m.Session + " while holding " + holdSession);
        parked = m;
        parkedAt = now;
        return false;
    }

    // Своя сессия замолчала — последнее отложенное состояние другой вступает в силу (старую реплику не говорим).
    void Unpark()
    {
        if (parked == null || (DateTime.Now - holdSeen).TotalSeconds <= HOLD_SILENCE) return;
        var m = parked;
        parked = null;
        m.Late = (DateTime.Now - parkedAt).TotalSeconds > 8;
        Report("session: " + holdSession + " silent, hold " + m.Session);
        holdSession = m.Session;
        holdSeen = DateTime.Now;
        holdBusy = m.Busy;
        Post(m);
    }

    StateMsg ParseState(string text)
    {
        var o = Json.Parse(text) as Dictionary<string, object>;
        if (o == null) return null;
        var m = new StateMsg();
        m.Seq = Str(o, "seq");
        m.Hero = Str(o, "hero");
        m.Name = Str(o, "name");
        m.Role = Str(o, "role");
        m.Line = Str(o, "line");
        m.Session = Str(o, "session");
        object busy = Val(o, "busy");
        m.Busy = busy is bool && (bool)busy;
        m.Timeline = StepsOf(Val(o, "timeline"));
        m.Loop = StepsOf(Val(o, "loop"));
        m.Enter = StepsOf(Val(o, "enter"));
        var pokesIn = Val(o, "pokes") as List<object>;
        if (pokesIn != null) foreach (var p in pokesIn) { var s = StepsOf(p); if (s.Count > 0) m.Pokes.Add(s); }
        m.PokeLines = Strings(Val(o, "pokeLines"));
        m.PokeMad = Strings(Val(o, "pokeMad"));
        var rosterIn = Val(o, "roster") as List<object>;
        if (rosterIn != null)
        {
            var key = new StringBuilder();
            foreach (var r in rosterIn)
            {
                var h = r as Dictionary<string, object>;
                if (h == null || string.IsNullOrEmpty(Str(h, "id"))) continue;
                var labels = new List<string>();
                var values = new List<double>();
                var stats = Val(h, "stats") as List<object>;
                if (stats != null)
                    foreach (var st in stats)
                    {
                        var a = st as List<object>;
                        if (a == null || a.Count < 2) continue;
                        labels.Add(Convert.ToString(a[0], CultureInfo.InvariantCulture));
                        values.Add(Num(a[1]));
                    }
                string sprite = Str(h, "sprite") ?? "";
                var hero = new Hero
                {
                    Id = Str(h, "id"), Sprite = sprite, Folder = sprite != "" ? HeroDir(sprite) : "", Name = Str(h, "name") ?? "",
                    Role = Str(h, "role") ?? "", About = Str(h, "about") ?? "", Labels = labels.ToArray(), Values = values.ToArray(),
                };
                m.Roster.Add(hero);
                key.Append(hero.Id).Append('|').Append(hero.Sprite).Append('|').Append(hero.Name).Append('|').Append(hero.Role).Append('|').Append(hero.About);
                for (int i = 0; i < labels.Count; i++) key.Append('|').Append(labels[i]).Append('=').Append(values[i].ToString("R", CultureInfo.InvariantCulture));
                key.Append('\n');
            }
            m.RosterKey = key.ToString();
        }
        m.Costar = ParseCostar(Val(o, "costar") as Dictionary<string, object>);
        // CHATS: chats — число открытых чатов (массив или число), busyChats — занятых. Нет поля — «—».
        object ch = Val(o, "chats"), bch = Val(o, "busyChats");
        if (ch is List<object>) m.Chats = ((List<object>)ch).Count;
        else if (ch != null) m.Chats = (int)Math.Round(Num(ch));
        if (bch is List<object>) m.BusyChats = ((List<object>)bch).Count;
        else if (bch != null) m.BusyChats = (int)Math.Round(Num(bch));
        // SPEND: spendRub (₽ за сегодня), spendBudgetRub (бюджет шкалы, по умолчанию 100), spendCurrency (не обязателен).
        object sr = Val(o, "spendRub");
        if (sr != null) m.SpendRub = Num(sr);
        object sb = Val(o, "spendBudgetRub");
        if (sb != null) { m.SpendBudgetRub = Num(sb); if (m.SpendBudgetRub <= 0) m.SpendBudgetRub = 100; }
        m.SpendCurrency = Str(o, "spendCurrency") ?? "";
        // BALANCE: остаток на балансе (₽), необязательная валюта; нет поля — «—».
        object br = Val(o, "balanceRub");
        if (br != null) m.BalanceRub = Num(br);
        m.BalanceCurrency = Str(o, "balanceCurrency") ?? "";
        return m;
    }

    Costar ParseCostar(Dictionary<string, object> c)
    {
        if (c == null) return null;
        string sprite = Str(c, "sprite");
        if (string.IsNullOrEmpty(sprite)) return null;
        var co = new Costar { Sprite = sprite, Name = Str(c, "name") ?? "", Folder = HeroDir(sprite) };
        var key = new StringBuilder(co.Folder);
        var stunts = Val(c, "stunts") as List<object>;
        if (stunts != null)
            foreach (var s in stunts)
            {
                var st = StepsOf(s);
                if (st.Count == 0) continue;
                co.Stunts.Add(st);
                key.Append(';');
                foreach (var x in st) key.Append(x.F).Append(':').Append(x.Ms).Append(',');
            }
        co.Key = key.ToString();
        return co.Stunts.Count > 0 ? co : null;
    }

    static object Val(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }

    static string Str(Dictionary<string, object> o, string k)
    {
        object v = Val(o, k);
        if (v is string) return (string)v;
        if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
        if (v is bool) return (bool)v ? "True" : "False";
        return null;
    }

    static double Num(object v)
    {
        if (v is double) return (double)v;
        double d;
        return v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    // Шаги [кадр, мс] или [кадр, мс, fx]; fx может не быть (старый state.json).
    static string[] Strings(object value)
    {
        var l = new List<string>();
        var list = value as List<object>;
        if (list != null) foreach (var v in list) { var t = v as string; if (!string.IsNullOrEmpty(t)) l.Add(t); }
        return l.ToArray();
    }

    static List<Step> StepsOf(object v)
    {
        var list = new List<Step>();
        var arr = v as List<object>;
        if (arr == null) return list;
        foreach (var item in arr)
        {
            var st = item as List<object>;
            if (st == null || st.Count < 2) continue;
            string f = st[0] as string;
            double ms = Num(st[1]);
            if (string.IsNullOrEmpty(f) || !(ms > 0)) continue;
            list.Add(new Step { F = f, Ms = ms, Fx = st.Count > 2 ? ParseFx(st[2] as string) : 0 });
        }
        return list;
    }

    // Разбор JSON без внешних библиотек: объект → Dictionary, массив → List<object>, число → double.
    sealed class Json
    {
        readonly string s;
        int i;
        Json(string text) { s = text; }

        public static object Parse(string text)
        {
            var j = new Json(text ?? "");
            j.Ws();
            var v = j.Value();
            j.Ws();
            if (j.i != j.s.Length) throw new FormatException("json: лишнее в конце");
            return v;
        }

        void Ws() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == '﻿')) i++; }

        void Expect(char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("json: ждали " + c);
            i++;
        }

        bool Word(string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        object Value()
        {
            if (i >= s.Length) throw new FormatException("json: обрыв");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>();
                Ws();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws();
                    string k = Str();
                    Ws();
                    Expect(':');
                    Ws();
                    d[k] = Value();
                    Ws();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect('}');
                    return d;
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Ws();
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value());
                    Ws();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(']');
                    return l;
                }
            }
            if (c == '"') return Str();
            if (Word("true")) return true;
            if (Word("false")) return false;
            if (Word("null")) return null;
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double n;
            if (i == st || !double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                throw new FormatException("json: не число");
            return n;
        }

        string Str()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("json: обрыв строки");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("json: обрыв строки");
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("json: обрыв строки");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
        }
    }

    // Готовые состояния — в окно (UI-поток): герой, цепочка, реакции, список героев, напарник, реплика.
    string seq, lastLine = "";
    void DrainInbox()
    {
        List<StateMsg> got = null;
        lock (inbox)
        {
            if (inbox.Count == 0) return;
            got = new List<StateMsg>(inbox);
            inbox.Clear();
        }
        foreach (var m in got)
            try { Apply(m); }
            catch (Exception e) { LastError = "state: " + e.Message; }
    }

    void Apply(StateMsg s)
    {
        // Сразу после выбора в меню мод ещё может прислать прежнего героя — ждём, пока он прочтёт choice.json.
        if (!StaleHero(s.Hero))
        {
            heroOn = !string.IsNullOrEmpty(s.Hero);
            HeroKey = s.Hero ?? "";
            if (!string.IsNullOrEmpty(s.Name)) HeroName = s.Name;
            HeroRole = s.Role ?? "";
            if (heroOn) SetHero(HeroDir(s.Hero));
        }
        if (s.Seq != seq) { seq = s.Seq; SetChain(s.Timeline, s.Loop); }
        enter = s.Enter;
        pokes.Clear();
        pokes.AddRange(s.Pokes);
        pokeLines = s.PokeLines;
        pokeMad = s.PokeMad;
        // Список героев меняется редко — пересобираем, только если он другой.
        if (s.RosterKey != rosterKey) { rosterKey = s.RosterKey; roster.Clear(); roster.AddRange(s.Roster); }
        SetCostar(s.Costar);
        // SPEND для панели обновляем в UI-потоке (в DrawMonitor читаются эти поля).
        spendRub = s.SpendRub; spendBudgetRub = s.SpendBudgetRub; spendCurrency = s.SpendCurrency ?? "";
        balanceRub = s.BalanceRub; balanceCurrency = s.BalanceCurrency ?? "";
        if (s.Line != lastLine) { lastLine = s.Line; if (!s.Late) Say(s.Line); }
    }

    // Раз в четверть секунды: жив ли мод, показаться/спрятаться, запросы проверки. Позиция питомца теперь
    // считается ТОЛЬКО от экрана: правый нижний угол рабочей области (с учётом таскбара), независимо от окон
    // DeepSeek Harness. Окно всегда TOPMOST, поэтому виден и при свёрнутом/закрытом Harness.
    int placed;
    string reportedError = "", reportedFeed = "", reportedLoad = "", hideWhy = "?";

    void Follow()
    {
        DrainInbox();
        UpdateTick();
        if (LastError != reportedError) { reportedError = LastError; Report("render: " + LastError); }
        string fe = feedError;
        if (fe != reportedFeed) { reportedFeed = fe; Report("feed: " + fe); }
        string le = loadError;
        if (le != reportedLoad) { reportedLoad = le; Report("frames: " + le); }
        // Выходим только если мод молчит дольше минуты (alive). Свёрнутое/закрытое окно Harness — не причина.
        if ((DateTime.Now - new DateTime(Interlocked.Read(ref aliveTicks))).TotalSeconds > 60) { Close(); return; }
        if (!heroOn)
        {
            placed = 0;
            if (hideWhy != "no-hero") { hideWhy = "no-hero"; Report("follow: hide (no-hero)"); }
            if (Visible) Hide();
            Evict();
            return;
        }
        if (hideWhy != "") { hideWhy = ""; Report("follow: show (screen corner)"); }
        // Дорожка и земля — вокруг домашней точки (правый нижний угол или место, куда перетащили).
        ApplyHomeArea();
        if (!Visible) Show();
        KeepOnTop();
        // Запросы проверки — только когда питомец уже нарисован (не в первый тик после запуска).
        if (++placed > 1) Requests();
        Evict();
    }

    // Проверка без экрана: снимок холста, меню с подсвеченной карточкой, выбор героя по номеру.
    void Requests()
    {
        try
        {
            if (File.Exists(menuRequest))
            {
                string txt = "";
                try { txt = File.ReadAllText(menuRequest).Trim(); } catch (Exception) { }
                File.Delete(menuRequest);
                int n;
                OpenMenu(int.TryParse(txt, out n) && n >= 0 ? n : -1);
                if (txt == "s") ShowSettings(true);
                // «p2» — сразу вторая страница героев.
                if (txt.StartsWith("p") && int.TryParse(txt.Substring(1), out n) && n >= 1) FlipPage(n - 1 - menuPage);
            }
            if (File.Exists(pickRequest))
            {
                string txt = "";
                try { txt = File.ReadAllText(pickRequest).Trim(); } catch (Exception) { }
                File.Delete(pickRequest);
                int n;
                if (int.TryParse(txt, out n)) Choose(n);
            }
            if (File.Exists(pokeRequest))
            {
                File.Delete(pokeRequest);
                Poke(new Point((int)fxCx, (int)(fxBase - 200 * fxKy)));
                Report("poke: " + (over != null ? over[0].F : "только реплика") + " // " + line);
            }
            if (File.Exists(recordRequest))
            {
                string txt = File.ReadAllText(recordRequest).Trim();
                File.Delete(recordRequest);
                var parts = txt.Split('|');
                StartRecord(parts[0], parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 8);
            }
            // Снимок окна панели (отдельный Hwnd): preview/mon-live.png — для тестов/проверки HUD.
            if (File.Exists(monRequest))
            {
                File.Delete(monRequest);
                if (monForm != null) monForm.SaveCanvas(Path.Combine(root, @"preview\mon-live.png"));
            }
            if (File.Exists(snapRequest))
            {
                File.Delete(snapRequest);
                Snapshot(snapPath);
                Report("snap: " + PerfText());
            }
        }
        catch (Exception e) { Report("request: " + e.Message); }
    }

    // ---- состояние движения и процедурной анимации ----
    double walkT, hopY, hopV, shake;
    double wanderTarget = double.NaN;
    bool wanderRun;
    DateTime calmSince = DateTime.Now;
    string lastGait;
    long lastKey = -1;
    DateTime errorUntil = DateTime.MinValue;

    // Последний сбой отрисовки (в stderr — %TEMP%\pet-err.txt).
    public string LastError = "";

    // Замер скорости: сколько длится кадр UI-потока (с разбором состояния), худший, сколько дольше 8 мс.
    // Строка — латиницей: stderr пишется в кодовой странице консоли.
    readonly Stopwatch perfWatch = new Stopwatch();
    double perfMax, perfSum, perfGap;
    int perfN, perfSlow;
    // Этапы кадра (мс, сумма): эффекты (расчёт), позади героя, карточка, герой, поверх героя, вывод на экран.
    readonly double[] stage = new double[6], frameStage = new double[6], worstStage = new double[6];
    long stageAt;
    int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
    void Stage(int i) { long t = perfWatch.ElapsedTicks; double d = (t - stageAt) * 1000.0 / Stopwatch.Frequency; stage[i] += d; frameStage[i] += d; stageAt = t; }

    string PerfText()
    {
        string s = string.Format(CultureInfo.InvariantCulture,
            "frames {0}, avg {1:0.00} ms, worst {2:0.0} ms, over 8 ms: {3}, longest gap {4:0} ms; particles {5}; hero frames {6}/{7}",
            perfN, perfN > 0 ? perfSum / perfN : 0, perfMax, perfSlow, perfGap * 1000, parts.Count,
            cur != null ? cur.ReadyCount : 0, cur != null ? cur.Names.Count : 0);
        if (perfN > 0)
            s += string.Format(CultureInfo.InvariantCulture, "; stages fx {0:0.00} back {1:0.00} card {2:0.00} hero {3:0.00} front {4:0.00} present {5:0.00}",
                stage[0] / perfN, stage[1] / perfN, stage[2] / perfN, stage[3] / perfN, stage[4] / perfN, stage[5] / perfN);
        s += string.Format(CultureInfo.InvariantCulture, "; worst frame: {0:0.0}/{1:0.0}/{2:0.0}/{3:0.0}/{4:0.0}/{5:0.0}; gc0 {6}, gc2 {7}; card builds {8}; interval {9} ms",
            worstStage[0], worstStage[1], worstStage[2], worstStage[3], worstStage[4], worstStage[5], GC.CollectionCount(0) - gc0, GC.CollectionCount(2) - gc2, chromeBuilds, frameTimer.Interval);
        chromeBuilds = 0;
        gc0 = GC.CollectionCount(0);
        gc2 = GC.CollectionCount(2);
        for (int i = 0; i < stage.Length; i++) { stage[i] = 0; worstStage[i] = 0; }
        perfN = 0; perfSum = 0; perfMax = 0; perfSlow = 0; perfGap = 0;
        return s;
    }

    // Сбой одного кадра не должен ронять отрисовку: секунду ждём и рисуем дальше.
    public void Tick(double dt)
    {
        perfWatch.Restart();
        for (int i = 0; i < frameStage.Length; i++) frameStage[i] = 0;
        DrainInbox();
        if (rescale.Count > 0)
            try { var d = rescale.Dequeue(); if (d.Owner != null && !d.Owner.Dead && sizePct != 100) BigOf(d); }
            catch (Exception e) { LastError = "scale: " + e.Message; }
        if (DateTime.Now >= errorUntil)
            try { Advance(dt); }
            catch (Exception e) { LastError = e.GetType().Name + ": " + e.Message; LogCrash("advance", e); errorUntil = DateTime.Now.AddSeconds(1); }
        double ms = perfWatch.Elapsed.TotalMilliseconds;
        if (Visible)
        {
            perfN++;
            perfSum += ms;
            if (ms > perfMax) { perfMax = ms; Array.Copy(frameStage, worstStage, frameStage.Length); }
            if (ms > 8) perfSlow++;
            if (perfN > 1 && dt > perfGap) perfGap = dt;
        }
    }

    void Advance(double dt)
    {
        if (cur == null || !hasArea || !Visible) return;
        dt = Math.Min(dt, 0.1);
        double now = Environment.TickCount / 1000.0;
        cur.Used = DateTime.Now;
        if (pendingLine != null && (DateTime.Now - lineAt).TotalMilliseconds >= MIN_LINE_MS) { string due = pendingLine; pendingLine = null; Say(due, true); }
        bool menu = UpdateMenu(dt);
        // Мини-режим: герой стоит на месте (аватарка), не гуляет и не играет телом.
        bool mini = miniOn && !menu;
        long key;
        var step = Current(out key);
        string f = Base(step.F);
        string clip = Clip(f);
        // Пока открыто меню, герой стоит на месте.
        bool chainMove = !menu && !mini && (clip == "walk" || clip == "run");
        bool calm = !menu && !mini && over == null && CALM.Contains(clip);

        // Скучно стоять — прогуляться к случайному месту вдоль окна; далеко — бегом.
        if (!calm) { wanderTarget = double.NaN; calmSince = DateTime.Now; }
        else if (wanderOn && double.IsNaN(wanderTarget) && (DateTime.Now - calmSince).TotalSeconds > 12 && rnd.NextDouble() < dt * 0.09 && maxX - minX > 160)
        {
            double to;
            do { to = minX + rnd.NextDouble() * (maxX - minX); } while (Math.Abs(to - x) < 120);
            wanderTarget = to;
            wanderRun = Math.Abs(to - x) > RUN_FROM;
        }

        bool wander = !double.IsNaN(wanderTarget);
        bool walking = chainMove || wander;
        if (walking)
        {
            // Бег без кадров бега — обычный шаг.
            bool run = (wander ? wanderRun : clip == "run") && Frames("run") >= 2;
            string gait = run ? "run" : "walk";
            double ms = run ? RUN_MS : WALK_MS;
            int n = Math.Max(2, Frames(gait));
            if (gait != lastGait) { walkT = 0; lastGait = gait; }
            walkT += dt * 1000;
            f = gait + "_" + ((int)(walkT / ms) % n + 1).ToString("00");
            if (wander) facing = wanderTarget > x ? 1 : -1;
            double move = Speed(gait, ms) * (sizePct / 100.0) * dt;
            if (wander && Math.Abs(wanderTarget - x) <= move)
            {
                x = wanderTarget;
                wanderTarget = double.NaN;
                calmSince = DateTime.Now;
            }
            else x += facing * move;
            if (x >= maxX) { x = maxX; facing = -1; }
            if (x <= minX) { x = minX; facing = 1; }
        }
        else lastGait = null;
        x = Math.Max(minX, Math.Min(maxX, x));
        // Во время драга герой строго под курсором (дом меняется на каждом MouseMove); в мини-режиме стоит ровно на доме.
        if (dragging || mini) x = homeLeft + W / 2;

        // Дальше — кадр, который реально будет на экране (у героя может не быть своего): по нему «толчок» и эффекты.
        f = Resolve(f);

        // Напарник в кадре — только при съёмке; герой тогда смотрит камерой на него (камера в кадрах — слева).
        bool coWant = !mini && CostarWanted(clip, menu, walking);
        if (coWant) facing = 1;

        // Эффект шага — с начала шага (подскок только с земли).
        int fx = walking ? 0 : step.Fx;
        if (key != lastKey)
        {
            lastKey = key;
            if ((fx & FX_HOP) != 0 && hopY > -1) hopV = -330;
        }
        hopV += 1500 * dt;
        hopY = Math.Min(0, hopY + hopV * dt);
        if (hopY == 0 && hopV > 0) hopV = 0;

        var img = Frame(f);
        // Кадров ещё нет совсем (первые миллисекунды после запуска) — ждём.
        if (img == null) return;

        // Дрожь: два несоизмеримых колебания — неровно, как от испуга.
        shake += (((fx & FX_SHAKE) != 0 || DateTime.Now < shakeUntil ? 2.0 : 0) - shake) * Math.Min(1, dt * 25);
        double shakeX = shake * (0.65 * Math.Sin(now * 97) + 0.35 * Math.Sin(now * 151));

        bool low = LOW.Contains(clip);
        // Ступни в кадре выше земли (прыжок нарисован) — тень меньше и бледнее, как при подскоке.
        double air = Math.Max(0, FRAME_G - (img.Box.Bottom - 1) - 3) / 90.0;
        double lift = Math.Min(1, air + -hopY / 140);

        // Адаптивный FPS: активной сцена считается при меню/драге/переходе напарника/подскоке/дрожи/эффектах клика/
        // меняющейся карточке/не-спокойном клипе; иначе 30 к/с (партиклы спокойного клипа на 30 к/с тоже нормально).
        bool calmClip = CALM.Contains(clip) || LOW.Contains(clip);
        bool cardBusy = false;
        if (!menu && DateTime.Now < sayUntil)
        {
            double cardAge = (DateTime.Now - sayAt).TotalMilliseconds, cardLeft = (sayUntil - DateTime.Now).TotalMilliseconds;
            cardBusy = cardAge < CARD_IN_MS + 150 || cardLeft < CARD_OUT_MS + 150
                || (lineAt > sayAt && (DateTime.Now - lineAt).TotalMilliseconds < TEXT_FADE_MS + 120);
        }
        bool sceneActive = menu || dragging || over != null || taps.Count > 0 || walking
            || hopY < -0.5 || Math.Abs(hopV) > 1 || shake > 0.05
            || coWant || coK > 0.01 || recFrames != null || cardBusy
            || (monForm != null && monForm.Dragging)
            || (!mini && !menu && !calmClip);
        SetFrameRate(sceneActive);
        Render(img, f, step.Fx, menu, low, lift, shakeX, now, dt, coWant);
    }

    int lastWidth = W, lastHeight = H;
    Rectangle winRect;

    void Render(FrameData img, string f, int stepFx, bool menu, bool low, double lift, double shakeX, double now, double dt, bool coWant)
    {
        int hx = (int)Math.Round(x);
        // Мини-режим: свой компактный рендер (аватарка-голова + карточка), окно — MINI_W x MINI_H.
        if (!menu && miniOn)
        {
            renderWindowLeft = MiniWinLeft();
            shown = img;
            stageAt = perfWatch.ElapsedTicks;
            Stage(0);
            RenderMini(img, f, dt, now);
            for (int i = 1; i < stage.Length; i++) Stage(i);
            return;
        }
        UpdateCostar(dt, coWant, hx);
        int ext = menu ? 0 : coExt;
        int left = menu ? menuLeft : hx - W / 2 - ext, width = menu ? cw : W + ext;
        g.ResetTransform();
        g.ResetClip();
        // Чистим только занятую часть холста (и то, что было занято в прошлом кадре).
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, Math.Max(width, lastWidth), H);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float cx = hx - left;
        recHx = hx;
        renderWindowLeft = left;

        // Где герой на холсте — для слоя эффектов.
        fxLeft = left;
        fxCx = (float)(cx + shakeX);
        fxBase = (float)(GROUND + hopY);
        double heroK = sizePct / 100.0;
        fxKx = fxKy = (float)heroK;
        fxFacing = facing;
        fxFw = img.FW;
        stageAt = perfWatch.ElapsedTicks;
        if (fxOn) try { UpdateFx(dt, f, stepFx, img, menu, low); }
        catch (Exception e) { LastError = "fx: " + e.Message; }
        Stage(0);

        // Мягкая тень: несколько эллипсов с убывающей плотностью; в воздухе — меньше и бледнее.
        float shW = (float)(128 * heroK * (1 - 0.45 * lift));
        using (var b = new SolidBrush(Color.FromArgb((int)(26 * (1 - 0.5 * lift)), 0, 0, 0)))
            for (int i = 3; i >= 1; i--)
                g.FillEllipse(b, cx - shW / 2 - i * 6, GROUND - 6 - i * 1.5f, shW + i * 12, 12 + i * 3);

        // Клик ловится по всей фигуре, а не только по закрашенным точкам: почти невидимая подложка закрывает щели
        // между рукой и телом, и клик «чуть мимо» не уходит в окно под героем.
        if (!menu && recFrames == null)
        {
            float bx0 = cx + facing * (img.Box.Left - img.FW / 2f) * (float)heroK, bx1 = cx + facing * (img.Box.Right - img.FW / 2f) * (float)heroK;
            float by0 = (float)(GROUND + hopY + (img.Box.Top - FRAME_G) * heroK), by1 = (float)(GROUND + hopY + (img.Box.Bottom - FRAME_G) * heroK);
            g.FillRectangle(HIT_AREA, RectangleF.FromLTRB(Math.Min(bx0, bx1) - HIT_PAD, by0 - HIT_PAD, Math.Max(bx0, bx1) + HIT_PAD, by1 + HIT_PAD));
        }

        // Позади героя: напарник на лесах, задний план эффектов (башня, софиты).
        try { DrawCostar(left, dt); }
        catch (Exception e) { LastError = "costar: " + e.Message; }
        if (fxOn) try { DrawFx(true); }
        catch (Exception e) { LastError = "fx: " + e.Message; }
        Stage(1);

        // Карточка реплики и герой — как в round 5; панель SYS.MON теперь отдельное окно (MonitorForm).
        if (!menu && DateTime.Now < sayUntil && recFrames == null) DrawCard(dt, RestHead(Clip(f), img), cx);
        Stage(2);
        // Кадр ложится на холст один к одному, по целым пикселям: картинка не пересчитывается, поэтому контур не дрожит и не рябит.
        var heroMode = g.InterpolationMode;
        var heroOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var pic = img.Img;
        int px = (int)Math.Round(cx + shakeX), py = (int)Math.Round(GROUND + hopY);
        int dx = img.OX - img.FW / 2, dy = img.OY - FRAME_G;
        if (sizePct != 100)
        {
            var big = BigOf(img);
            pic = big.Img;
            dx = big.X - (int)Math.Round(img.FW * heroK / 2);
            dy = big.Y - (int)Math.Round(FRAME_G * heroK);
        }
        int iw = pic.Width, ih = pic.Height;
        if (facing > 0) g.DrawImage(pic, new Rectangle(px + dx, py + dy, iw, ih), 0, 0, iw, ih, GraphicsUnit.Pixel);
        else
        {
            var state = g.Save();
            g.TranslateTransform(px, py);
            g.ScaleTransform(-1, 1);
            g.DrawImage(pic, new Rectangle(dx, dy, iw, ih), 0, 0, iw, ih, GraphicsUnit.Pixel);
            g.Restore(state);
        }
        g.InterpolationMode = heroMode;
        g.PixelOffsetMode = heroOffset;
        shown = img;
        Stage(3);

        // Поверх героя: частицы и HUD.
        if (fxOn) try { DrawFx(false); }
        catch (Exception e) { LastError = "fx: " + e.Message; }
        if (taps.Count > 0) try { DrawTaps(dt); }
        catch (Exception e) { LastError = "tap: " + e.Message; }
        Stage(4);
        g.ResetTransform();
        g.ResetClip();
        if (menu) DrawMenu(now);
        Present(left, groundY - GROUND, width, H);
        Stage(5);
    }

    // Карточка реплики: срезанные углы, неоновое свечение, портрет, шапка. Для глаз спокойная: стоит неподвижно (не парит
    // и не прыгает за макушкой каждого кадра), появляется и уходит плавно, текст виден сразу целиком и сменяется мягко.
    static readonly HashSet<string> SIT_POSE = new HashSet<string> { "sit", "yawn", "doze" };
    static readonly HashSet<string> LIE_POSE = new HashSet<string> { "lie", "sleep", "liedown", "fallasleep", "ld", "fa" };

    // Макушка в обычной позе героя (стоя, сидя или лёжа) — от неё считается высота карточки.
    float RestHead(string clip, FrameData img)
    {
        var rest = cur.Get(LIE_POSE.Contains(clip) ? "lie_01" : SIT_POSE.Contains(clip) ? "sit_01" : "idle_01") ?? img;
        int top = rest.Box.Top;
        // Сидя герой зевает, подняв руки: карточка стоит выше них с самого начала.
        if (SIT_POSE.Contains(clip)) { var yawn = cur.Get("yawn_01"); if (yawn != null) top = Math.Min(top, yawn.Box.Top); }
        return (float)(GROUND - (FRAME_G - top) * (sizePct / 100.0));
    }

    double cardY = double.NaN;
    readonly ImageAttributes fadeAttr = new ImageAttributes();
    readonly ColorMatrix fadeMatrix = new ColorMatrix();

    void DrawFaded(Bitmap b, int x0, int y0, double a)
    {
        if (a <= 0) return;
        var r = new Rectangle(x0, y0, b.Width, b.Height);
        if (a >= 0.995) { g.DrawImage(b, r, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel); return; }
        fadeMatrix.Matrix33 = (float)a;
        fadeAttr.SetColorMatrix(fadeMatrix);
        g.DrawImage(b, r, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, fadeAttr);
    }



    // Речевая карточка: имя/роль + текст, БЕЗ аватарки (портрет остался только в карточке звонка).
    // Размеры и отступы подобраны так, чтобы текст занимал всю ширину и не оставлял «дырки» слева.
    const int CARD_TXT_W = 340, CARD_TXT_H = 104;

    void DrawCard(double dt, float head, float cx, bool mini = false)
    {
        var t = DateTime.Now;
        double age = (t - sayAt).TotalMilliseconds;
        double a = Smooth(Math.Min(age / CARD_IN_MS, (sayUntil - t).TotalMilliseconds / CARD_OUT_MS));
        int w = CARD_TXT_W, h = CARD_TXT_H;
        // Кончик хвостика — в 10 пикселях над макушкой обычной позы. Новая карточка сразу на месте; сменилась поза — плавно
        // переезжает. Когда в кадре напарник (он стоит левее и выше), поднимается, чтобы не закрывать ему лицо.
        double target = Math.Max(4, head - 10 - 16 - h - 90 * Ease(coK));
        if (double.IsNaN(cardY) || age < 60) cardY = target;
        else cardY += (target - cardY) * Math.Min(1, dt * 5);
        int y0 = (int)Math.Round(cardY), x0 = mini ? (MINI_W - w) / 2 : (int)Math.Round(cx - w / 2.0);
        if (mini)
        {
            // В мини-режиме окно узкое и стоит у края экрана — карточка не должна уезжать за экран.
            var scr = Screen.PrimaryScreen.WorkingArea;
            int minX = scr.Left - renderWindowLeft + 8;
            int maxX = scr.Right - renderWindowLeft - w - 8;
            if (maxX < minX) maxX = minX;
            x0 = Math.Max(minX, Math.Min(maxX, x0));
        }
        var chrome = CardChrome();
        var mode = g.InterpolationMode;
        var offset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        DrawFaded(chrome, x0 - CARD_PAD, y0 - CARD_PAD, a);
        int tx = x0 + 16, ty = y0 + 30, tw = w - 32, th = h - 36;
        if (cardTextKey != line)
        {
            if (cardPrevText != null) cardPrevText.Dispose();
            cardPrevText = cardText;
            cardText = BuildCardText(line, tw, th);
            cardTextKey = line;
        }
        double mix = lineAt > sayAt ? Smooth((t - lineAt).TotalMilliseconds / TEXT_FADE_MS) : 1;
        if (cardPrevText != null && mix < 1) DrawFaded(cardPrevText, tx - 2, ty, a * (1 - mix));
        DrawFaded(cardText, tx - 2, ty, a * mix);
        g.InterpolationMode = mode;
        g.PixelOffsetMode = offset;
    }

    // Текст карточки — готовой картинкой: разметка строк одна и та же, буквы не переезжают.
    Bitmap cardText, cardPrevText;
    string cardTextKey;

    Bitmap BuildCardText(string text, int w, int h)
    {
        var bmp = new Bitmap(w + 4, h, PixelFormat.Format32bppPArgb);
        using (var tg = Graphics.FromImage(bmp))
        {
            tg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            tg.DrawString(text, fontText, B(PALE), new RectangleF(2, 0, w, h));
        }
        return bmp;
    }

    // Каркас карточки (рамка со свечением, акцентная полоса, шапка «ИМЯ // РОЛЬ») рисуется в картинку один раз —
    // на кадр идут только эта картинка и печатающийся текст. Аватарки в карточке нет.
    const int CARD_PAD = 16;
    Bitmap cardChrome;
    string cardKey;
    int chromeBuilds;

    Bitmap CardChrome()
    {
        // Шапка: «ИМЯ // РОЛЬ», без роли — «ВХОДЯЩЕЕ».
        string name = (string.IsNullOrEmpty(HeroName) ? Path.GetFileName(dir) ?? "" : HeroName).ToUpperInvariant();
        string tag = string.IsNullOrEmpty(HeroRole) ? Tr("ВХОДЯЩЕЕ", "INCOMING") : HeroRole.ToUpperInvariant();
        string key = name + "|" + tag;
        if (cardChrome != null && key == cardKey) return cardChrome;
        if (cardChrome != null) cardChrome.Dispose();
        float w = CARD_TXT_W, h = CARD_TXT_H, c = 14, x0 = CARD_PAD, y0 = CARD_PAD, tail = x0 + w / 2 + 10;
        var bmp = new Bitmap((int)w + 2 * CARD_PAD, (int)h + 16 + 2 * CARD_PAD, PixelFormat.Format32bppPArgb);
        using (var cg = Graphics.FromImage(bmp))
        {
            cg.SmoothingMode = SmoothingMode.AntiAlias;
            cg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            cg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var pts = new[]
            {
                new PointF(x0 + c, y0), new PointF(x0 + w, y0), new PointF(x0 + w, y0 + h - c), new PointF(x0 + w - c, y0 + h),
                new PointF(tail + 12, y0 + h), new PointF(tail, y0 + h + 16), new PointF(tail - 4, y0 + h), new PointF(x0, y0 + h), new PointF(x0, y0 + c),
            };
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                for (int i = 4; i >= 1; i--)
                    using (var p = new Pen(Color.FromArgb(22, RED), i * 3.5f)) cg.DrawPath(p, path);
                using (var fill = new SolidBrush(Color.FromArgb(238, 10, 14, 19))) cg.FillPath(fill, path);
                using (var p = new Pen(RED, 2)) cg.DrawPath(p, path);
            }
            // Мелкий декор по нижней кромке: жёлтый тик и красный штрих. Верхнюю акцентную линию и левый
            // бирюзовый маркер не рисуем — пользователь просил убрать обе полоски (вместе со свечением).
            using (var b = new SolidBrush(RED)) cg.FillRectangle(b, x0 + w - 46, y0 + h - 6, 30, 3);
            using (var b = new SolidBrush(YELLOW)) cg.FillRectangle(b, x0 + w - 178, y0 + h - 7, 12, 4);
            using (var b = new SolidBrush(CYAN)) cg.DrawString(name + "  //  " + tag, fontHead, b, x0 + 16, y0 + 10);
        }
        cardChrome = bmp;
        cardKey = key;
        chromeBuilds++;
        return bmp;
    }

    // ==== слой эффектов: частицы и HUD-элементы по клипу и герою ====
    // Эффект выбирается по клипу текущего кадра (часть имени до «_») и герою (имя папки кадров): сначала ищется
    // «герой:клип», потом просто «клип». Смена клипа — прежний эффект гаснет, новый проявляется (~200 мс).
    // Добавить эффект — строчка в BuildFx: клипы и три функции (любая может быть null): «каждый кадр» (эмиттер
    // частиц, состояние), «позади героя» и «поверх героя» (HUD). Координаты — в кадре героя (FP), отражение по
    // направлению взгляда и подскок учитываются сами; лицо не закрываем: значки — сбоку от головы.
    delegate void FxFn(FxRun r);
    sealed class FxDef { public FxFn Tick, Back, Front; }
    sealed class FxRun
    {
        public string Key;
        public FxDef Def;
        // Время с начала клипа, видимость (0..1), метка события (например, вспышки), счётчик.
        public double T, K, Mark = -10;
        public readonly double[] Acc = new double[4];
        public bool Dying, FrameNew;
        public string Frame = "";
        public int N;
        public object Data;
    }
    sealed class Particle
    {
        public FxRun Owner;
        public int Kind;
        public bool Back;
        public double X, Y, VX, VY, Grav, Drag, Age, Life, Size, Grow, Rot, VRot, Wob, Seed;
        public Color C;
        public string Text;
    }
    const int MAX_PARTS = 150;
    const int P_DOT = 0, P_SPARK = 1, P_CONF = 2, P_GLYPH = 3, P_NOTE = 4, P_Z = 5, P_BUBBLE = 6, P_DROP = 7, P_PUFF = 8, P_LINE = 9,
        P_RING = 10, P_HEART = 11, P_ICON = 12, P_GEAR = 13, P_BOLT = 14, P_SHURI = 15, P_BAR = 16, P_STREAK = 17, P_HEX = 18;
    const string HEXCH = "0123456789ABCDEF";
    static readonly string[] CODE = { "{}", "</>", "01", ";", "=>", "()", "[]", "#", "&&", "fn", "0x", "!=" };
    static readonly string[] READ_LINES = { "fn load(cfg) {", "  if (!ok) return", "  map → filter", "// TODO: test", "await sync()", "x = 0x2F;", "return data", "}" };
    readonly Dictionary<string, FxDef> fxTable = new Dictionary<string, FxDef>();
    readonly List<FxRun> runs = new List<FxRun>();
    readonly List<Particle> parts = new List<Particle>();
    readonly Stack<Particle> spare = new Stack<Particle>();
    readonly SolidBrush fxB = new SolidBrush(Color.White);
    readonly Pen fxP = new Pen(Color.White, 1), fxDash = new Pen(Color.White, 1) { DashStyle = DashStyle.Dash };
    readonly PointF[] q3 = new PointF[3], q4 = new PointF[4], q6 = new PointF[6];
    readonly Dictionary<string, SizeF> sizes = new Dictionary<string, SizeF>();
    double fxDt, fxA = 1, fxMaster = 1;
    float fxCx, fxBase = GROUND, fxKx = 1, fxKy = 1, fxFw = 286;
    int fxFacing = -1, fxLeft;
    string fxHero = "";
    // Сглаженные опоры в координатах кадра: середина головы, макушка, рамка фигуры, ступни.
    double aHeadX = 143, aTop = 60, aL = 80, aR = 206, aBottom = FRAME_G, aFeetX = 143;
    bool aSet, fxLow;

    // Клипы одной строки делят один эффект: при смене клипа внутри группы (film ↔ an) он не перезапускается.
    void Fx(string clips, FxFn tick, FxFn back, FxFn front)
    {
        var def = new FxDef { Tick = tick, Back = back, Front = front };
        foreach (var k in clips.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) fxTable[k] = def;
    }

    static FxFn Both(FxFn a, FxFn b) { return r => { a(r); b(r); }; }

    // Таблица эффектов: клип (или «герой:клип») → эмиттер, задний план, передний план.
    void BuildFx()
    {
        // Общие — у всех героев.
        Fx("think", null, null, ThinkFront);
        Fx("surprised", null, null, SurprisedFront);
        Fx("scared", ScaredTick, null, ScaredFront);
        Fx("angry", AngryTick, null, AngryFront);
        Fx("facepalm", null, null, GloomFront);
        Fx("sad", SadTick, null, SadFront);
        Fx("happy celebrate clap", BurstTick, null, null);
        Fx("dance", DanceTick, DanceBack, null);
        Fx("music", MusicTick, null, null);
        Fx("sleep fallasleep fa", SleepTick, null, null);
        Fx("doze", DozeTick, null, null);
        Fx("sneeze", SneezeTick, null, null);
        Fx("drink", DrinkTick, null, null);
        Fx("phone", PhoneTick, null, null);
        Fx("selfie", FlashTick, null, FlashFront);
        Fx("run", RunTick, null, null);
        Fx("walk", WalkTick, null, null);
        Fx("wave hello bow", SparkTick, null, null);
        Fx("type", TypeTick, null, null);
        Fx("read", null, null, ReadFront);
        Fx("scan", ScanTick, null, ScanFront);
        // Съёмка (film, an): видоискатель — у Романа и у всех, у кого нет своего.
        Fx("film an", null, null, CamFront);
        // Роман, блогер с камерой: «лайки» и счётчик зрителей при празднике.
        Fx("kadr:happy kadr:celebrate kadr:clap", Both(BurstTick, LikesTick), null, LikesFront);
        // Талия, нетраннер-ниндзя: взлом башни, голо-печать, сюрикен-искры, красные глитчи.
        Fx("talia:film", HackTick, HackBack, HackFront);
        Fx("talia:selfie", SealTick, null, SealFront);
        Fx("talia:happy talia:celebrate talia:clap", Both(ShurikenBurstTick, GlitchTick), null, null);
        Fx("talia:wave talia:hello talia:bow", ShurikenTick, null, null);
        Fx("talia:surprised", GlitchTick, null, SurprisedFront);
        Fx("talia:scared", Both(ScaredTick, GlitchTick), null, ScaredFront);
        Fx("talia:angry", Both(AngryTick, GlitchTick), null, AngryFront);
        // Ди, механик-изобретатель: ремонт дрона и шестерёнки при празднике.
        Fx("di:film", RepairTick, null, RepairFront);
        Fx("di:happy di:celebrate di:clap", Both(BurstTick, GearBurstTick), null, null);
        // Уик, фиксер: тактическая карта из очков, монета при празднике, дымок от скуки.
        Fx("wick:film", null, null, MapFront);
        Fx("wick:happy wick:celebrate wick:clap", null, null, CoinFront);
        Fx("wick:bored", SmokeTick, null, null);
    }

    string PickFx(string f, string clip, int stepFx)
    {
        // Проснулся с испугу на кадре засыпания — без «Z»; засыпание (fa) — «Z» только когда глаза уже закрыты.
        if ((clip == "fallasleep" || clip == "fa") && (stepFx & FX_SHAKE) != 0) return null;
        if (clip == "fa" && FrameNo(f) < 4) return null;
        string k = fxHero + ":" + clip;
        if (fxTable.ContainsKey(k)) return k;
        return fxTable.ContainsKey(clip) ? clip : null;
    }

    void UpdateFx(double dt, string f, int stepFx, FrameData fd, bool menu, bool low)
    {
        fxDt = dt;
        fxLow = low;
        fxHero = cur != null ? Path.GetFileName(cur.Folder).ToLowerInvariant() : "";
        // Опоры по кадру — сглаженно: значки не дёргаются вместе с позой.
        double k = aSet ? Math.Min(1, dt * 12) : 1;
        aSet = true;
        aHeadX += (fd.HeadX - aHeadX) * k;
        aTop += (fd.Box.Top - aTop) * k;
        aL += (fd.Box.Left - aL) * k;
        aR += (fd.Box.Right - aR) * k;
        aBottom += (fd.Box.Bottom - aBottom) * k;
        if (fd.Feet.Width > 0) aFeetX += (fd.Feet.X + fd.Feet.Width / 2.0 - aFeetX) * k;
        // Открыто меню — эффекты гаснут.
        fxMaster = Math.Max(0, Math.Min(1, fxMaster + (menu ? -dt : dt) / 0.2));
        string key = PickFx(f, Clip(f), stepFx);
        FxRun live = null;
        FxDef want = key != null ? fxTable[key] : null;
        foreach (var r in runs)
            if (!r.Dying) { if (r.Def == want) live = r; else r.Dying = true; }
        if (live == null && key != null) runs.Add(new FxRun { Key = key, Def = fxTable[key] });
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var r = runs[i];
            r.T += dt;
            r.K = Math.Max(0, Math.Min(1, r.K + (r.Dying ? -dt : dt) / 0.2));
            r.FrameNew = r.Frame != f;
            r.Frame = f;
            if (r.Dying && r.K <= 0) { runs.RemoveAt(i); continue; }
            if (!r.Dying && !menu && r.Def.Tick != null) r.Def.Tick(r);
        }
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var p = parts[i];
            p.Age += dt;
            if (p.Age >= p.Life || (p.Owner.Dying && p.Owner.K <= 0)) { parts.RemoveAt(i); spare.Push(p); continue; }
            p.VY += p.Grav * dt;
            if (p.Drag > 0) { double d = Math.Max(0, 1 - p.Drag * dt); p.VX *= d; p.VY *= d; }
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            p.Rot += p.VRot * dt;
            p.Size = Math.Max(0, p.Size + p.Grow * dt);
        }
    }

    // Задний план: HUD позади героя, потом частицы позади; передний: частицы, потом HUD поверх.
    void DrawFx(bool back)
    {
        if (back)
            foreach (var r in runs)
                if (r.Def.Back != null) { fxA = r.K * fxMaster; if (fxA > 0.01) r.Def.Back(r); }
        DrawParticles(back);
        if (!back)
            foreach (var r in runs)
                if (r.Def.Front != null) { fxA = r.K * fxMaster; if (fxA > 0.01) r.Def.Front(r); }
    }

    // Точка кадра героя (fx, fy) на холсте: с отражением по взгляду, подскоком, шагом и дрожью.
    PointF FP(double fx, double fy)
    {
        return new PointF((float)(fxCx + fxFacing * (fx - fxFw / 2) * fxKx), (float)(fxBase + (fy - FRAME_G) * fxKy));
    }

    // Прямоугольник кадра на холсте (при отражении — тот же прямоугольник зеркально, текст в нём не отражён).
    RectangleF Rect(double fx, double fy, double w, double h)
    {
        var a = FP(fx, fy);
        var b = FP(fx + w, fy + h);
        return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    // Скорость вдоль кадра (+ — вправо по рисунку) на холсте.
    double VX(double frameV) { return fxFacing * frameV * fxKx; }

    Particle Emit(FxRun r, int kind, PointF at, double vx, double vy, double life, double size, Color c)
    {
        if (parts.Count >= MAX_PARTS) return null;
        var p = spare.Count > 0 ? spare.Pop() : new Particle();
        p.Owner = r; p.Kind = kind; p.Back = false;
        p.X = at.X + fxLeft; p.Y = at.Y; p.VX = vx; p.VY = vy;
        p.Grav = 0; p.Drag = 0; p.Age = 0; p.Life = life; p.Size = size; p.Grow = 0;
        p.Rot = 0; p.VRot = 0; p.Wob = 0; p.Seed = rnd.NextDouble() * 100;
        p.C = c; p.Text = null;
        parts.Add(p);
        return p;
    }

    // Сколько частиц выпустить в этом кадре при perSec в секунду (накопитель — свой у каждого эмиттера).
    int Rate(FxRun r, int slot, double perSec)
    {
        r.Acc[slot] += perSec * fxDt;
        int n = (int)r.Acc[slot];
        r.Acc[slot] -= n;
        return n;
    }

    double Rnd(double a, double b) { return a + rnd.NextDouble() * (b - a); }
    double Side() { return rnd.Next(2) == 0 ? -1 : 1; }
    Color AnyOf(params Color[] cs) { return cs[rnd.Next(cs.Length)]; }
    static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
    static int A255(double a) { return Math.Max(0, Math.Min(255, (int)(a * 255))); }
    static Color Al(Color c, double a) { return Color.FromArgb(A255(a), c.R, c.G, c.B); }
    // Цвет HUD с учётом видимости эффекта.
    Color Ca(Color c, double a) { return Al(c, a * fxA); }
    SolidBrush B(Color c) { fxB.Color = c; return fxB; }
    Pen P(Color c, float w) { fxP.Color = c; fxP.Width = w; return fxP; }

    SizeF Measure(string t, Font f)
    {
        string k = f.Name + f.Size + "|" + t;
        SizeF s;
        if (sizes.TryGetValue(k, out s)) return s;
        if (sizes.Count > 400) sizes.Clear();
        s = g.MeasureString(t, f, PointF.Empty, tight);
        sizes[k] = s;
        return s;
    }

    // Текст с тёмной обводкой: читается и на светлом, и на тёмном.
    void Outlined(string t, Font f, float x0, float y0, Color c, double a, Color edge)
    {
        var eb = B(Al(edge, a * 0.8));
        g.DrawString(t, f, eb, x0 - 1.2f, y0, tight);
        g.DrawString(t, f, eb, x0 + 1.2f, y0, tight);
        g.DrawString(t, f, eb, x0, y0 - 1.2f, tight);
        g.DrawString(t, f, eb, x0, y0 + 1.2f, tight);
        g.DrawString(t, f, B(Al(c, a)), x0, y0, tight);
    }

    // Значок-текст по центру точки, с масштабом.
    void Glyph(string t, Font f, PointF at, float scale, Color c, double a, Color edge)
    {
        var sz = Measure(t, f);
        var st = g.Save();
        g.TranslateTransform(at.X, at.Y);
        if (scale != 1) g.ScaleTransform(scale, scale);
        Outlined(t, f, -sz.Width / 2, -sz.Height / 2, c, a, edge);
        g.Restore(st);
    }

    void DrawParticles(bool back)
    {
        foreach (var p in parts)
        {
            if (p.Back != back) continue;
            double life = p.Age / p.Life;
            double a = p.Owner.K * fxMaster;
            if (p.Kind != P_BAR) a *= Math.Min(1, p.Age / 0.08) * Math.Min(1, (1 - life) / 0.35);
            if (a <= 0.01) continue;
            float x0 = (float)(p.X - fxLeft + (p.Wob > 0 ? Math.Sin(p.Age * p.Wob + p.Seed) * 4 : 0)), y0 = (float)p.Y, s = (float)p.Size;
            switch (p.Kind)
            {
                case P_DOT: g.FillEllipse(B(Al(p.C, a)), x0 - s, y0 - s, 2 * s, 2 * s); break;
                case P_SPARK: Spark(x0, y0, (float)(s * (0.75 + 0.25 * Math.Sin(p.Age * 18 + p.Seed))), p.C, a); break;
                case P_CONF: Confetti(x0, y0, s, p.Rot, p.Seed, p.C, a); break;
                case P_GLYPH: g.DrawString(p.Text, fxGlyph, B(Al(p.C, a)), x0 - s, y0, tight); break;
                case P_HEX: g.DrawString(p.Text, fxGlyph, B(Al(p.C, a)), x0 - 3, y0, tight); break;
                case P_NOTE: Note(x0, y0, s, p.C, a, ((int)p.Seed) % 2); break;
                case P_Z:
                    {
                        var st = g.Save();
                        g.TranslateTransform(x0, y0);
                        g.ScaleTransform(s, s);
                        Outlined(p.Text, fxZ, -6, -9, p.C, a, INK);
                        g.Restore(st);
                        break;
                    }
                case P_BUBBLE:
                    g.DrawEllipse(P(Al(p.C, a * 0.9), 1.1f), x0 - s, y0 - s, 2 * s, 2 * s);
                    g.FillEllipse(B(Al(PALE, a * 0.8)), x0 - s * 0.5f, y0 - s * 0.6f, s * 0.55f, s * 0.55f);
                    break;
                case P_DROP: Drop(x0, y0, s, p.C, a); break;
                case P_PUFF:
                    // Мягкое облачко: светлый ореол и плотнее середина — кучевой формы, без колец.
                    Cloud(x0, y0 + s * 0.3f, s * 2.6f, p.C, a * 0.18);
                    Cloud(x0, y0 + s * 0.2f, s * 1.9f, p.C, a * 0.3);
                    break;
                case P_LINE: g.DrawLine(P(Al(p.C, a), 1.4f), x0, y0, x0 + (float)p.Rot * s, y0); break;
                case P_RING: g.DrawEllipse(P(Al(p.C, a * (1 - life)), 1.4f), x0 - s, y0 - s, 2 * s, 2 * s); break;
                case P_HEART: Heart(x0, y0, s, p.C, a); break;
                case P_ICON: NotifyIcon(x0, y0, s * (float)Math.Min(1, 0.5 + p.Age / 0.15), ((int)p.Seed) % 4, a); break;
                case P_GEAR: Gear(x0, y0, s, p.Rot, p.C, a); break;
                case P_BOLT: Bolt(x0, y0, s, p.Rot, p.C, a); break;
                case P_SHURI: Shuriken(x0, y0, s, p.Rot, p.C, a); break;
                case P_BAR: g.FillRectangle(B(Al(p.C, a * 0.85)), x0, y0, s, 2.5f); break;
                case P_STREAK:
                    {
                        // Искра сварки: короткий след вдоль скорости.
                        float ex = x0 - (float)(p.VX * 0.03), ey = y0 - (float)(p.VY * 0.03);
                        g.DrawLine(P(Al(p.C, a * 0.5), 2.4f), x0, y0, ex, ey);
                        g.DrawLine(P(Al(Color.White, a), 1f), x0, y0, ex, ey);
                        break;
                    }
            }
        }
    }

    // ---- рисунки частиц и значков ----
    // Искра: четыре луча-ромба и светлая середина.
    void Spark(float x0, float y0, float r, Color c, double a)
    {
        if (r < 0.5f) return;
        g.FillEllipse(B(Al(c, a * 0.22)), x0 - r, y0 - r, r * 2, r * 2);
        var b = B(Al(c, a));
        float t = r * 0.24f;
        q4[0] = new PointF(x0, y0 - r * 1.7f); q4[1] = new PointF(x0 + t, y0); q4[2] = new PointF(x0, y0 + r * 1.7f); q4[3] = new PointF(x0 - t, y0);
        g.FillPolygon(b, q4);
        q4[0] = new PointF(x0 - r * 1.7f, y0); q4[1] = new PointF(x0, y0 - t); q4[2] = new PointF(x0 + r * 1.7f, y0); q4[3] = new PointF(x0, y0 + t);
        g.FillPolygon(b, q4);
        g.FillEllipse(B(Al(Color.White, a)), x0 - t, y0 - t, 2 * t, 2 * t);
    }

    // Конфетти: прямоугольник, крутится в воздухе (ширина «мигает» от поворота).
    void Confetti(float x0, float y0, float s, double rot, double seed, Color c, double a)
    {
        double cs = Math.Cos(rot), sn = Math.Sin(rot);
        float hw = s, hh = s * 0.45f * (float)Math.Abs(Math.Cos(rot * 0.7 + seed)) + 0.6f;
        q4[0] = new PointF((float)(x0 - hw * cs + hh * sn), (float)(y0 - hw * sn - hh * cs));
        q4[1] = new PointF((float)(x0 + hw * cs + hh * sn), (float)(y0 + hw * sn - hh * cs));
        q4[2] = new PointF((float)(x0 + hw * cs - hh * sn), (float)(y0 + hw * sn + hh * cs));
        q4[3] = new PointF((float)(x0 - hw * cs - hh * sn), (float)(y0 - hw * sn + hh * cs));
        g.FillPolygon(B(Al(c, a)), q4);
    }

    void Heart(float x0, float y0, float s, Color c, double a)
    {
        using (var path = new GraphicsPath(FillMode.Winding))
        {
            path.AddEllipse(x0 - s * 1.0f, y0 - s * 0.74f, s * 1.04f, s * 1.04f);
            path.AddEllipse(x0 - s * 0.04f, y0 - s * 0.74f, s * 1.04f, s * 1.04f);
            path.AddPolygon(new[] { new PointF(x0 - s * 0.97f, y0 - s * 0.08f), new PointF(x0 + s * 0.97f, y0 - s * 0.08f), new PointF(x0, y0 + s * 1.0f) });
            g.FillPath(B(Al(c, a)), path);
        }
        g.FillEllipse(B(Al(Color.White, a * 0.65)), x0 - s * 0.68f, y0 - s * 0.5f, s * 0.36f, s * 0.3f);
    }

    // Нота: головка, штиль и флажок; или две ноты под ребром.
    void Note(float x0, float y0, float s, Color c, double a, int kind)
    {
        var b = B(Al(c, a));
        if (kind == 0)
        {
            g.FillEllipse(b, x0 - s * 0.62f, y0 - s * 0.42f, s * 1.24f, s * 0.84f);
            g.FillRectangle(b, x0 + s * 0.42f, y0 - s * 2.2f, s * 0.22f, s * 2.2f);
            q4[0] = new PointF(x0 + s * 0.6f, y0 - s * 2.2f); q4[1] = new PointF(x0 + s * 1.3f, y0 - s * 1.45f);
            q4[2] = new PointF(x0 + s * 1.15f, y0 - s * 1.25f); q4[3] = new PointF(x0 + s * 0.6f, y0 - s * 1.7f);
            g.FillPolygon(b, q4);
        }
        else
        {
            g.FillEllipse(b, x0 - s * 1.3f, y0 - s * 0.3f, s * 1.1f, s * 0.75f);
            g.FillEllipse(b, x0 + s * 0.3f, y0 - s * 0.55f, s * 1.1f, s * 0.75f);
            g.FillRectangle(b, x0 - s * 0.38f, y0 - s * 2.0f, s * 0.2f, s * 2.0f);
            g.FillRectangle(b, x0 + s * 1.22f, y0 - s * 2.25f, s * 0.2f, s * 2.0f);
            q4[0] = new PointF(x0 - s * 0.38f, y0 - s * 2.0f); q4[1] = new PointF(x0 + s * 1.42f, y0 - s * 2.25f);
            q4[2] = new PointF(x0 + s * 1.42f, y0 - s * 1.8f); q4[3] = new PointF(x0 - s * 0.38f, y0 - s * 1.55f);
            g.FillPolygon(b, q4);
        }
    }

    // Капля: круглый низ и острый верх (пот, дождик).
    void Drop(float x0, float y0, float s, Color c, double a)
    {
        using (var path = new GraphicsPath(FillMode.Winding))
        {
            path.AddEllipse(x0 - s, y0 - s, 2 * s, 2 * s);
            path.AddPolygon(new[] { new PointF(x0 - s * 0.94f, y0 - s * 0.3f), new PointF(x0 + s * 0.94f, y0 - s * 0.3f), new PointF(x0, y0 - s * 2.4f) });
            g.FillPath(B(Al(c, a)), path);
        }
        g.FillEllipse(B(Al(Color.White, a * 0.8)), x0 - s * 0.5f, y0 - s * 0.35f, s * 0.42f, s * 0.42f);
    }

    void Gear(float x0, float y0, float r, double rot, Color c, double a)
    {
        const int teeth = 8;
        var pts = new PointF[teeth * 4];
        double w = Math.PI / teeth;
        for (int i = 0; i < teeth; i++)
        {
            double a0 = rot + i * 2 * Math.PI / teeth;
            pts[i * 4] = Polar(x0, y0, r * 0.74, a0 - w * 0.62);
            pts[i * 4 + 1] = Polar(x0, y0, r, a0 - w * 0.34);
            pts[i * 4 + 2] = Polar(x0, y0, r, a0 + w * 0.34);
            pts[i * 4 + 3] = Polar(x0, y0, r * 0.74, a0 + w * 0.62);
        }
        g.FillPolygon(B(Al(c, a)), pts);
        g.FillEllipse(B(Al(INK, a)), x0 - r * 0.3f, y0 - r * 0.3f, r * 0.6f, r * 0.6f);
    }

    void Bolt(float x0, float y0, float r, double rot, Color c, double a)
    {
        for (int i = 0; i < 6; i++) q6[i] = Polar(x0, y0, r, rot + i * Math.PI / 3);
        g.FillPolygon(B(Al(c, a)), q6);
        g.FillEllipse(B(Al(INK, a)), x0 - r * 0.4f, y0 - r * 0.4f, r * 0.8f, r * 0.8f);
    }

    // Сюрикен: четыре лезвия и дырка посередине.
    void Shuriken(float x0, float y0, float r, double rot, Color c, double a)
    {
        var pts = new PointF[8];
        for (int i = 0; i < 8; i++) pts[i] = Polar(x0, y0, i % 2 == 0 ? r : r * 0.32, rot + i * Math.PI / 4);
        g.FillPolygon(B(Al(c, a)), pts);
        g.FillEllipse(B(Al(INK, a)), x0 - r * 0.16f, y0 - r * 0.16f, r * 0.32f, r * 0.32f);
    }

    static PointF Polar(float x0, float y0, double r, double ang) { return new PointF((float)(x0 + Math.Cos(ang) * r), (float)(y0 + Math.Sin(ang) * r)); }

    void Cloud(float x0, float y0, float w, Color c, double a)
    {
        using (var path = new GraphicsPath(FillMode.Winding))
        {
            path.AddEllipse(x0 - w * 0.5f, y0 - w * 0.14f, w * 0.48f, w * 0.34f);
            path.AddEllipse(x0 - w * 0.26f, y0 - w * 0.32f, w * 0.5f, w * 0.5f);
            path.AddEllipse(x0 + 0.02f * w, y0 - w * 0.2f, w * 0.48f, w * 0.38f);
            g.FillPath(B(Al(c, a)), path);
        }
    }

    // Значок уведомления: тёмная плашка с бирюзовой рамкой и символ — сердце, сообщение, колокольчик, «+1».
    void NotifyIcon(float x0, float y0, float s, int kind, double a)
    {
        g.FillRectangle(B(Al(INK, a * 0.85)), x0 - s, y0 - s, 2 * s, 2 * s);
        g.DrawRectangle(P(Al(CYAN, a), 1.2f), x0 - s, y0 - s, 2 * s, 2 * s);
        switch (kind)
        {
            case 0: Heart(x0, y0 + s * 0.05f, s * 0.55f, RED, a); break;
            case 1:
                g.FillRectangle(B(Al(PALE, a)), x0 - s * 0.6f, y0 - s * 0.45f, s * 1.2f, s * 0.72f);
                q3[0] = new PointF(x0 - s * 0.3f, y0 + s * 0.26f); q3[1] = new PointF(x0 - s * 0.05f, y0 + s * 0.26f); q3[2] = new PointF(x0 - s * 0.4f, y0 + s * 0.6f);
                g.FillPolygon(B(Al(PALE, a)), q3);
                break;
            case 2:
                g.FillEllipse(B(Al(YELLOW, a)), x0 - s * 0.42f, y0 - s * 0.6f, s * 0.84f, s * 0.9f);
                g.FillRectangle(B(Al(YELLOW, a)), x0 - s * 0.55f, y0 + s * 0.05f, s * 1.1f, s * 0.25f);
                g.FillEllipse(B(Al(YELLOW, a)), x0 - s * 0.14f, y0 + s * 0.32f, s * 0.28f, s * 0.28f);
                break;
            default:
                var sz = Measure("+1", fxSmall);
                g.DrawString("+1", fxSmall, B(Al(YELLOW, a)), x0 - sz.Width / 2, y0 - sz.Height / 2, tight);
                break;
        }
        g.FillEllipse(B(Al(RED, a)), x0 + s * 0.55f, y0 - s * 1.35f, s * 0.8f, s * 0.8f);
    }

    // Уголки-скобки по углам прямоугольника.
    void Brackets(RectangleF r, float len, Color c, float w)
    {
        var pen = P(c, w);
        q3[0] = new PointF(r.X, r.Y + len); q3[1] = new PointF(r.X, r.Y); q3[2] = new PointF(r.X + len, r.Y);
        g.DrawLines(pen, q3);
        q3[0] = new PointF(r.Right - len, r.Y); q3[1] = new PointF(r.Right, r.Y); q3[2] = new PointF(r.Right, r.Y + len);
        g.DrawLines(pen, q3);
        q3[0] = new PointF(r.X, r.Bottom - len); q3[1] = new PointF(r.X, r.Bottom); q3[2] = new PointF(r.X + len, r.Bottom);
        g.DrawLines(pen, q3);
        q3[0] = new PointF(r.Right - len, r.Bottom); q3[1] = new PointF(r.Right, r.Bottom); q3[2] = new PointF(r.Right, r.Bottom - len);
        g.DrawLines(pen, q3);
    }

    // Отрезок в координатах кадра.
    void Seg(double x0, double y0, double x1, double y1, Color c, double a, float w)
    {
        g.DrawLine(P(Ca(c, a), w), FP(x0, y0), FP(x1, y1));
    }

    // Голо-панель: тёмное полупрозрачное стекло, сканлайны, светящаяся рамка со срезанными углами;
    // раскрывается по высоте (open 0..1). Возвращает прямоугольник на холсте.
    RectangleF Panel(RectangleF r, Color edge, double open)
    {
        if (open <= 0.01) return RectangleF.Empty;
        float hh = (float)(r.Height * Ease(open));
        var rr = new RectangleF(r.X, r.Y + (r.Height - hh) / 2, r.Width, Math.Max(1.5f, hh));
        float c = Math.Min(7, rr.Height / 2);
        q6[0] = new PointF(rr.X + c, rr.Y); q6[1] = new PointF(rr.Right, rr.Y); q6[2] = new PointF(rr.Right, rr.Bottom - c);
        q6[3] = new PointF(rr.Right - c, rr.Bottom); q6[4] = new PointF(rr.X, rr.Bottom); q6[5] = new PointF(rr.X, rr.Y + c);
        g.FillPolygon(B(Ca(INK, 0.66)), q6);
        g.FillPolygon(B(Ca(edge, 0.07)), q6);
        var sl = B(Ca(Color.White, 0.045));
        for (float y = rr.Y + 2; y < rr.Bottom - 1; y += 3) g.FillRectangle(sl, rr.X + 1, y, rr.Width - 2, 1);
        g.DrawPolygon(P(Ca(edge, 0.2), 3.5f), q6);
        g.DrawPolygon(P(Ca(edge, 0.95), 1.2f), q6);
        return rr;
    }

    // ---- общие эффекты ----
    // Мысли: цепочка пузырьков от головы и пульсирующий «?».
    void ThinkFront(FxRun r)
    {
        double cyc = (r.T % 1.5) / 1.5;
        for (int i = 0; i < 3; i++)
        {
            var p = FP(aHeadX + 34 + i * 9, aTop + 16 - i * 10);
            double on = Smooth((cyc * 4 - i) / 0.6);
            if (on <= 0.01) continue;
            float rad = 2.2f + i * 1.3f;
            g.FillEllipse(B(Ca(PALE, on * 0.9)), p.X - rad, p.Y - rad, rad * 2, rad * 2);
            g.DrawEllipse(P(Ca(CYAN, on), 1), p.X - rad, p.Y - rad, rad * 2, rad * 2);
        }
        float s = (float)(1 + 0.12 * Math.Sin(r.T * 6));
        Glyph("?", fxEmote, FP(aHeadX + 66, aTop - 8), s, YELLOW, fxA, INK);
    }

    // «!» выскакивает сбоку над головой, с лучиками удивления.
    void Exclaim(FxRun r, Color c, double tremble)
    {
        double t = r.T;
        float s = t < 0.12 ? (float)(0.4 + t / 0.12 * 0.9) : (float)(1 + 0.3 * Math.Exp(-(t - 0.12) * 9) * Math.Cos((t - 0.12) * 22));
        var q = FP(aHeadX + 60, aTop - 4);
        q.X += (float)(Math.Sin(t * 57) * tremble);
        Glyph("!", fxEmote, q, s, c, fxA, INK);
        if (t < 0.6)
        {
            double a = 1 - t / 0.6;
            var hc = FP(aHeadX, aTop + 30);
            for (int i = 0; i < 3; i++)
            {
                double ang = -Math.PI / 2 + fxFacing * (0.3 + i * 0.42);
                g.DrawLine(P(Ca(YELLOW, a), 2.2f), Polar(hc.X, hc.Y, 46 * fxKx, ang), Polar(hc.X, hc.Y, 60 * fxKx, ang));
            }
        }
    }

    void SurprisedFront(FxRun r) { Exclaim(r, YELLOW, 0); }

    void ScaredFront(FxRun r)
    {
        Exclaim(r, RED, 1.5);
        // Большая капля пота у виска медленно сползает.
        double cyc = (r.T % 1.1) / 1.1;
        var d = FP(aHeadX - 36, aTop + 18 + cyc * 14);
        Drop(d.X, d.Y, 5.5f, SWEAT, fxA * (1 - Math.Max(0, cyc - 0.7) / 0.3));
    }

    void ScaredTick(FxRun r)
    {
        for (int n = Rate(r, 0, 3.2); n > 0; n--)
        {
            var p = Emit(r, P_DROP, FP(aHeadX + Side() * 32, aTop + 24), 0, 30, 0.6, 2.4, SWEAT);
            if (p != null) p.Grav = 260;
        }
    }

    void AngryFront(FxRun r)
    {
        var c = FP(aHeadX + 40, aTop + 14);
        AngerMark(c, (float)(1.35 + 0.18 * Math.Sin(r.T * 9)));
    }

    // Значок злости: четыре дужки вокруг центра, вогнутой стороной наружу.
    void AngerMark(PointF c, float s)
    {
        float k = 6.5f * s, rr = 5.5f * s;
        for (int pass = 0; pass < 2; pass++)
        {
            var pen = pass == 0 ? P(Ca(INK, 0.6), 4.6f * s) : P(Ca(RED, 1), 2.6f * s);
            g.DrawArc(pen, c.X - k - rr, c.Y - k - rr, 2 * rr, 2 * rr, 0, 90);
            g.DrawArc(pen, c.X + k - rr, c.Y - k - rr, 2 * rr, 2 * rr, 90, 90);
            g.DrawArc(pen, c.X + k - rr, c.Y + k - rr, 2 * rr, 2 * rr, 180, 90);
            g.DrawArc(pen, c.X - k - rr, c.Y + k - rr, 2 * rr, 2 * rr, 270, 90);
        }
    }

    // Пар от макушки: облачка в обе стороны.
    void AngryTick(FxRun r)
    {
        for (int n = Rate(r, 0, 4); n > 0; n--)
        {
            double side = (r.N++ % 2 == 0) ? -1 : 1;
            var p = Emit(r, P_PUFF, FP(aHeadX + side * 24, aTop + 4), VX(side * 45), -70, 0.7, 5, SMOKE);
            if (p != null) { p.Drag = 1.5; p.Grow = 22; }
        }
    }

    // Линии уныния: синие штрихи над головой, медленно сползают.
    void GloomFront(FxRun r)
    {
        for (int i = 0; i < 5; i++)
        {
            double len = 26 + (i % 2) * 8;
            double y0 = aTop - 20 + ((r.T * 10 + i * 5) % 8);
            var p0 = FP(aHeadX - 26 + i * 13, y0);
            var p1 = FP(aHeadX - 26 + i * 13, y0 + len);
            using (var lg = new LinearGradientBrush(new PointF(0, p0.Y - 1), new PointF(0, p1.Y + 1), Ca(BLUE, 0), Ca(BLUE, 0.85)))
                g.FillRectangle(lg, p0.X - 1.2f, p0.Y, 2.4f, p1.Y - p0.Y);
        }
    }

    // Тучка сбоку над головой и дождик из неё.
    PointF CloudPoint(FxRun r) { var c = FP(aHeadX + 56, aTop - 6); c.Y += (float)(Math.Sin(r.T * 2.4) * 2); return c; }

    void SadFront(FxRun r)
    {
        var c = CloudPoint(r);
        Cloud(c.X + 1, c.Y + 1.5f, 42, INK, fxA * 0.4);
        Cloud(c.X, c.Y, 42, Color.FromArgb(120, 140, 175), fxA * 0.95);
    }

    void SadTick(FxRun r)
    {
        var c = CloudPoint(r);
        for (int n = Rate(r, 0, 7); n > 0; n--)
            Emit(r, P_DROP, new PointF(c.X + (float)Rnd(-14, 14), c.Y + 8), 0, 85, 0.45, 1.5, BLUE);
    }

    // Праздник: всплеск конфетти и искр в начале клипа.
    void BurstTick(FxRun r)
    {
        if (r.T > 0.3) return;
        for (int n = Rate(r, 0, 80); n > 0; n--)
        {
            double side = Side();
            bool conf = rnd.NextDouble() < 0.65;
            var p = Emit(r, conf ? P_CONF : P_SPARK, FP(aHeadX + side * Rnd(22, 50), aTop + Rnd(0, 40)), VX(side * Rnd(40, 170)), Rnd(-260, -120),
                Rnd(1.1, 1.7), conf ? Rnd(3, 5) : Rnd(2.5, 4), AnyOf(RED, CYAN, YELLOW, PALE));
            if (p == null) return;
            p.Grav = conf ? 420 : 300;
            p.Drag = 1.2;
            p.Rot = Rnd(0, 6.28);
            p.VRot = Rnd(-12, 12);
        }
    }

    // Танец: неоновые софиты сверху качаются и мерцают; на полу — пятна света.
    static readonly Color[] BEAMS = { RED, CYAN, YELLOW };
    void DanceBack(FxRun r)
    {
        float floor = fxBase - 4, top = Math.Max(0, fxBase - 480);
        for (int i = 0; i < 3; i++)
        {
            float sx0 = fxCx + (i - 1) * 150;
            float tx = fxCx + (i - 1) * 50 + (float)(Math.Sin(r.T * (0.9 + i * 0.27) + i * 2.1) * 110);
            double fl = 0.8 + 0.2 * Math.Sin(r.T * 23 + i * 5);
            q4[0] = new PointF(sx0 - 5, top); q4[1] = new PointF(sx0 + 5, top); q4[2] = new PointF(tx + 46, floor); q4[3] = new PointF(tx - 46, floor);
            using (var lg = new LinearGradientBrush(new PointF(0, top - 1), new PointF(0, floor + 1), Ca(BEAMS[i], 0.3 * fl), Ca(BEAMS[i], 0.05 * fl)))
                g.FillPolygon(lg, q4);
            g.FillEllipse(B(Ca(BEAMS[i], 0.3 * fl)), tx - 50, floor - 7, 100, 14);
            g.FillEllipse(B(Ca(Color.White, 0.6 * fl)), sx0 - 4, top - 2, 8, 6);
        }
    }

    void DanceTick(FxRun r)
    {
        for (int n = Rate(r, 0, 1.8); n > 0; n--)
        {
            double side = Side();
            var p = Emit(r, P_NOTE, FP(aHeadX + side * 40, aTop + 30), VX(side * Rnd(14, 30)), -Rnd(40, 55), 1.5, 4.5, AnyOf(CYAN, YELLOW, RED, PALE));
            if (p != null) p.Wob = 5;
        }
        for (int n = Rate(r, 1, 4); n > 0; n--)
        {
            double ang = Rnd(0, Math.PI * 2), rad = Rnd(80, 125);
            var c = FP(aHeadX, aTop + 120);
            Emit(r, P_SPARK, Polar(c.X, c.Y, rad, ang), 0, -10, 0.6, 3, AnyOf(CYAN, YELLOW, PALE));
        }
    }

    // Музыка в наушниках: ноты плывут вверх то с одной, то с другой стороны головы.
    void MusicTick(FxRun r)
    {
        for (int n = Rate(r, 0, 2.2); n > 0; n--)
        {
            double side = (r.N++ % 2 == 0) ? -1 : 1;
            var p = Emit(r, P_NOTE, FP(aHeadX + side * 38, aTop + 30), VX(side * Rnd(14, 26)), -Rnd(42, 54), 1.7, 4.5, AnyOf(CYAN, YELLOW, RED, PALE));
            if (p != null) p.Wob = 5;
        }
    }

    // Сон: «z» и «Z» поднимаются от головы и растут.
    void Zzz(FxRun r, double perSec, double size, double grow)
    {
        for (int n = Rate(r, 0, perSec); n > 0; n--)
        {
            var p = Emit(r, P_Z, FP(aHeadX + 16, aTop + (fxLow ? 6 : 2)), VX(Rnd(14, 22)), -Rnd(24, 32), 2.4, size, r.N % 2 == 0 ? PALE : CYAN);
            if (p == null) return;
            p.Text = r.N++ % 2 == 0 ? "z" : "Z";
            p.Grow = grow;
            p.Wob = 2.5;
        }
    }

    void SleepTick(FxRun r) { if (r.T < 0.05 && r.N == 0) r.Acc[0] = 0.8; Zzz(r, 1.15, 0.6, 0.35); }

    void DozeTick(FxRun r) { Zzz(r, 0.75, 0.45, 0.25); }

    // Чих: облачко брызг вперёд на кадре sneeze_03.
    void SneezeTick(FxRun r)
    {
        if (!r.FrameNew || r.Frame != "sneeze_03") return;
        var at = FP(aHeadX - 30, aTop + 50);
        for (int i = 0; i < 9; i++)
        {
            double ang = Rnd(-0.7, 0.7), sp = Rnd(80, 170);
            var p = Emit(r, P_PUFF, at, VX(-Math.Cos(ang) * sp), Math.Sin(ang) * sp, Rnd(0.5, 0.8), Rnd(4, 7), PALE);
            if (p != null) { p.Drag = 3; p.Grow = 26; }
        }
        for (int i = 0; i < 4; i++) Emit(r, P_DOT, at, VX(-Rnd(160, 240)), Rnd(-60, 40), 0.35, 1.3, SWEAT);
    }

    // Газировка: пузырьки от банки уходят вбок, мимо лица.
    void DrinkTick(FxRun r)
    {
        for (int n = Rate(r, 0, 5.5); n > 0; n--)
        {
            var p = Emit(r, P_BUBBLE, FP(aHeadX - 28 + Rnd(-3, 3), aTop + 86), VX(-Rnd(25, 45)), -Rnd(35, 55), Rnd(0.7, 1.0), Rnd(1.6, 3.4), CYAN);
            if (p != null) p.Wob = 7;
        }
    }

    // Телефон: значки уведомлений вылетают из экрана наружу.
    void PhoneTick(FxRun r)
    {
        if (r.T < 0.05 && r.N == 0) { r.N = 1; r.Acc[0] = 0.9; }
        for (int n = Rate(r, 0, 1.3); n > 0; n--)
            Emit(r, P_ICON, FP(aHeadX + 38, aTop + 70), VX(Rnd(22, 40)), -Rnd(30, 45), 1.6, 7.5, PALE);
    }

    // Селфи: вспышка камеры на кадре selfie_02.
    PointF CamPoint() { return FP(aHeadX - 66, aTop + 35); }

    void FlashTick(FxRun r)
    {
        if (!r.FrameNew || r.Frame != "selfie_02") return;
        r.Mark = r.T;
        var c = CamPoint();
        for (int i = 0; i < 6; i++)
        {
            double ang = Rnd(0, Math.PI * 2), sp = Rnd(60, 140);
            Emit(r, P_SPARK, c, Math.Cos(ang) * sp, Math.Sin(ang) * sp, 0.5, 3.5, AnyOf(PALE, YELLOW));
        }
    }

    void FlashFront(FxRun r)
    {
        double t = r.T - r.Mark;
        if (r.Mark < 0 || t > 0.4) return;
        double a = 1 - t / 0.4;
        var c = CamPoint();
        float rad = (float)(16 + 40 * Math.Sqrt(t / 0.4));
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(c.X - rad, c.Y - rad, rad * 2, rad * 2);
            using (var pg = new PathGradientBrush(path))
            {
                pg.CenterColor = Ca(Color.White, a);
                pg.SurroundColors = new[] { Ca(Color.White, 0) };
                g.FillPath(pg, path);
            }
        }
        var pen = P(Ca(Color.White, a * 0.9), 1.6f);
        g.DrawLine(pen, c.X - (float)(70 * a), c.Y, c.X + (float)(70 * a), c.Y);
        g.DrawLine(pen, c.X, c.Y - (float)(40 * a), c.X, c.Y + (float)(40 * a));
    }

    // Бег: пыль из-под ног на касании и линии скорости позади.
    void RunTick(FxRun r)
    {
        if (r.FrameNew && FrameNo(r.Frame) % 4 == 1)
            for (int i = 0; i < 2; i++)
            {
                var p = Emit(r, P_PUFF, FP(aFeetX - Rnd(0, 30), FRAME_G - 3), VX(-Rnd(10, 50)), -Rnd(10, 30), 0.5, 4, DUST);
                if (p != null) { p.Drag = 2; p.Grow = 20; }
            }
        for (int n = Rate(r, 1, 14); n > 0; n--)
        {
            var p = Emit(r, P_LINE, FP(aL - Rnd(5, 40), aTop + Rnd(40, 230)), 0, 0, 0.22, Rnd(18, 46), AnyOf(PALE, PALE, CYAN));
            if (p != null) p.Rot = -fxFacing;
        }
    }

    // Шаг: лёгкая пыль на касании.
    void WalkTick(FxRun r)
    {
        if (!r.FrameNew || FrameNo(r.Frame) % 4 != 1) return;
        var p = Emit(r, P_PUFF, FP(aFeetX - 10, FRAME_G - 3), VX(-Rnd(8, 25)), -Rnd(6, 16), 0.45, 3, DUST);
        if (p != null) { p.Drag = 2; p.Grow = 12; }
    }

    // Привет, поклон: пара искр у головы, на длинном приветствии — ещё по одной.
    void Twinkles(FxRun r, int kind, Color[] colors)
    {
        int n = r.N == 0 ? 3 : 0;
        r.N = 1;
        n += Rate(r, 0, 0.9);
        for (; n > 0; n--)
        {
            double ang = -Math.PI / 2 + Rnd(-1.2, 1.2), rad = Rnd(48, 72);
            var c = FP(aHeadX, aTop + 34);
            var p = Emit(r, kind, Polar(c.X, c.Y, rad * fxKx, ang), Rnd(-10, 10), -Rnd(10, 24), Rnd(0.6, 0.9), kind == P_SHURI ? Rnd(3.5, 5) : Rnd(2.8, 4), colors[rnd.Next(colors.Length)]);
            if (p != null) p.VRot = Rnd(-10, 10);
        }
    }

    static readonly Color[] SPARKS = { YELLOW, PALE, CYAN };
    void SparkTick(FxRun r) { Twinkles(r, P_SPARK, SPARKS); }

    // Печать на голо-клавиатуре: глифы кода поднимаются от неё (у пояса) и гаснут, не доходя до лица.
    void TypeTick(FxRun r)
    {
        double kb = aTop + 0.47 * (FRAME_G - aTop);
        for (int n = Rate(r, 0, 9); n > 0; n--)
        {
            var c = fxHero == "talia" ? AnyOf(RED, CYAN, CYAN, PALE) : AnyOf(CYAN, CYAN, YELLOW, PALE);
            var p = Emit(r, P_GLYPH, FP(aHeadX + Rnd(-72, 72), kb + Rnd(-4, 4)), 0, -Rnd(36, 50), Rnd(0.8, 1.1), 6, c);
            if (p != null) p.Text = CODE[rnd.Next(CODE.Length)];
        }
    }

    // Чтение: бегущая строка-сканер по голо-планшету у груди и мелкие строки текста рядом.
    void ReadFront(FxRun r)
    {
        double tx = aHeadX + 24, ty = aTop + 100;
        var tab = Rect(tx - 38, ty - 22, 76, 44);
        float sy0 = tab.Y + (float)(((r.T * 0.8) % 1) * tab.Height);
        using (var lg = new LinearGradientBrush(new PointF(0, sy0 - 13), new PointF(0, sy0 + 1), Ca(CYAN, 0), Ca(CYAN, 0.28)))
            g.FillRectangle(lg, tab.X, sy0 - 12, tab.Width, 12);
        g.DrawLine(P(Ca(CYAN, 0.6), 3f), tab.X, sy0, tab.Right, sy0);
        g.DrawLine(P(Ca(Color.White, 0.95), 1.2f), tab.X, sy0, tab.Right, sy0);
        var pr = Panel(Rect(tx + 46, ty - 36, 70, 46), CYAN, (r.T - 0.1) / 0.25);
        if (r.T < 0.36) return;
        int first = (int)(r.T * 2.5);
        var st = g.Save();
        g.SetClip(pr);
        float shift = (float)((r.T * 2.5 % 1) * 9);
        for (int i = 0; i < 5; i++)
        {
            string s = READ_LINES[(first + i) % READ_LINES.Length];
            g.DrawString(s, fxMono, B(Ca(i % 2 == 0 ? PALE : CYAN, 0.85)), pr.X + 5, pr.Y + 4 + i * 9 - shift, tight);
        }
        g.Restore(st);
    }

    // Поиск: круги радара от головы и метка цели, которая то и дело переезжает и захватывается.
    void ScanTick(FxRun r)
    {
        for (int n = Rate(r, 0, 2.2); n > 0; n--)
        {
            var p = Emit(r, P_RING, FP(aHeadX, aTop + 30), 0, 0, 1.25, 38 * fxKx, CYAN);
            if (p != null) p.Grow = 115;
        }
    }

    void ScanFront(FxRun r)
    {
        var st = r.Data as double[];
        if (st == null || r.T - st[2] > 1.7)
        {
            st = new[] { Side() * Rnd(110, 150), Rnd(-10, 90), r.T };
            r.Data = st;
        }
        double t = r.T - st[2];
        var c = FP(aHeadX + st[0], aTop + st[1]);
        float s = (float)(26 - 14 * Ease(t / 0.35));
        bool locked = t > 0.35;
        double a = locked && ((int)(t / 0.15)) % 2 == 0 ? 0.55 : 1;
        var col = locked ? RED : YELLOW;
        Brackets(new RectangleF(c.X - s, c.Y - s, 2 * s, 2 * s), 6, Ca(col, a), 1.6f);
        var pen = P(Ca(col, a * 0.8), 1);
        g.DrawLine(pen, c.X - 4, c.Y, c.X + 4, c.Y);
        g.DrawLine(pen, c.X, c.Y - 4, c.X, c.Y + 4);
        if (locked) Outlined(Tr("ЦЕЛЬ", "TARGET"), fxTiny, c.X + s + 4, c.Y - 5, RED, fxA, INK);
    }

    // Съёмка: уголки видоискателя вокруг героя (с напарником в кадре — вокруг напарника), мигающий «● REC»,
    // таймкод 00:ММ:СС:КК, батарейка и лёгкое зерно (мимо лица).
    void CamFront(FxRun r)
    {
        var p0 = FP(aL, aTop);
        var p1 = FP(aR, FRAME_G);
        var rc = RectangleF.FromLTRB(Math.Min(p0.X, p1.X) - 16, p0.Y - 14, Math.Max(p0.X, p1.X) + 16, p1.Y + 6);
        if (coK > 0 && coRectSet)
        {
            var co = new RectangleF((float)(coRectW.X - fxLeft) - 12, coRectW.Y - 12, coRectW.Width + 24, coRectW.Height + 18);
            double k = Ease(coK);
            rc = RectangleF.FromLTRB(Lerp(rc.Left, co.Left, k), Lerp(rc.Top, co.Top, k), Lerp(rc.Right, co.Right, k), Lerp(rc.Bottom, co.Bottom, k));
        }
        var shadow = rc;
        shadow.Offset(1, 1);
        Brackets(shadow, 20, Ca(INK, 0.5), 3.2f);
        Brackets(rc, 20, Ca(PALE, 0.92), 2f);
        if (((int)(r.T / 0.5)) % 2 == 0) g.FillEllipse(B(Ca(RED, 1)), rc.X + 9, rc.Y + 10, 8, 8);
        Outlined("REC", fxMid, rc.X + 21, rc.Y + 6, PALE, fxA, INK);
        int fr = (int)(r.T * 24);
        string tc = "00:" + (fr / 1440 % 60).ToString("00") + ":" + (fr / 24 % 60).ToString("00") + ":" + (fr % 24).ToString("00");
        Outlined(tc, fxMid, rc.X + 9, rc.Bottom - 21, PALE, fxA, INK);
        // Батарейка.
        float bx = rc.Right - 32, by = rc.Y + 10;
        g.DrawRectangle(P(Ca(PALE, 0.9), 1.2f), bx, by, 18, 9);
        g.FillRectangle(B(Ca(PALE, 0.9)), bx + 18, by + 3, 2, 3);
        for (int i = 0; i < 3; i++) g.FillRectangle(B(Ca(i == 0 && ((int)(r.T / 0.8)) % 2 == 0 ? RED : PALE, 0.9)), bx + 2 + i * 5.3f, by + 2, 4, 5.5f);
        // Зерно.
        var face = Rect(aHeadX - 36, aTop - 4, 72, 78);
        for (int i = 0; i < 46; i++)
        {
            float gx = rc.X + (float)rnd.NextDouble() * rc.Width, gy = rc.Y + (float)rnd.NextDouble() * rc.Height;
            if (face.Contains(gx, gy)) continue;
            g.FillRectangle(B(Ca(rnd.Next(2) == 0 ? Color.White : Color.Black, Rnd(0.12, 0.3))), gx, gy, 1.3f, 1.3f);
        }
    }

    static float Lerp(float a, float b, double k) { return (float)(a + (b - a) * k); }

    // ---- Роман: «лайки» и счётчик зрителей ----
    void LikesTick(FxRun r)
    {
        if (r.T > 1.4) return;
        for (int n = Rate(r, 1, 7); n > 0; n--)
        {
            double side = Side();
            var p = Emit(r, P_HEART, FP(aHeadX + side * Rnd(44, 72), aTop + Rnd(30, 80)), VX(side * 18), -Rnd(50, 70), 1.4, Rnd(5, 8), AnyOf(RED, RED, PINK));
            if (p != null) p.Wob = 6;
        }
    }

    void LikesFront(FxRun r)
    {
        double k = Ease(r.T / 0.25);
        var at = FP(aHeadX + 76, aTop + 30);
        float w = 98, h = 22;
        float px = fxFacing > 0 ? at.X : at.X - w;
        float py = at.Y - (float)(6 * (1 - k));
        g.FillRectangle(B(Ca(INK, 0.85 * k)), px, py, w, h);
        g.DrawRectangle(P(Ca(RED, k), 1.2f), px, py, w, h);
        if (((int)(r.T / 0.5)) % 2 == 0) g.FillEllipse(B(Ca(RED, k)), px + 6, py + 8, 6, 6);
        g.DrawString(Tr("ЭФИР", "LIVE"), fxTiny, B(Ca(RED, k)), px + 15, py + 7, tight);
        // Глаз — «зрители».
        float ex = px + 47, ey = py + 11;
        g.DrawEllipse(P(Ca(PALE, k), 1.2f), ex - 6, ey - 3.5f, 12, 7);
        g.FillEllipse(B(Ca(PALE, k)), ex - 2, ey - 2, 4, 4);
        int viewers = 1024 + (int)(r.T * r.T * 900 + r.T * 300);
        g.DrawString(viewers.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' '), fxSmall, B(Ca(YELLOW, k)), ex + 9, py + 5.5f, tight);
    }

    // ---- Талия: взлом башни корпорации ----
    // Взлом идёт по кадрам film: на film_05 (красное «!» на голо-экране) — тревога льда, на film_07 (зелёная галка)
    // слой льда снят; после трёх слоёв — «ДОСТУП ОТКРЫТ». Шкала растёт за три цикла клипа.
    sealed class HackState
    {
        public int Layer, Idx = 1;
        public double Shown, AlertAt = -10, BreakAt = -10, AccessAt = -10, HexT;
        public readonly string[] Hex = new string[5];
    }

    string HexLine()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3; i++) sb.Append(HEXCH[rnd.Next(16)]);
        sb.Append(' ');
        for (int i = 0; i < 4; i++) sb.Append(' ').Append(HEXCH[rnd.Next(16)]).Append(HEXCH[rnd.Next(16)]);
        return sb.ToString();
    }

    HackState Hack(FxRun r)
    {
        var h = r.Data as HackState;
        if (h != null) return h;
        h = new HackState();
        for (int i = 0; i < h.Hex.Length; i++) h.Hex[i] = HexLine();
        r.Data = h;
        return h;
    }

    // Голо-экран у запястья в кадрах film (координаты кадра): справа от него — панели (ниже карточки реплики,
    // в стороне от лица), от него вверх — поток символов.
    const double SCR_X = 195, SCR_Y = 128;
    const double PA_X = 246, PA_Y = 56, PB_X = 254, PB_Y = 116, PC_X = 240, PC_Y = 178;

    void HackTick(FxRun r)
    {
        var h = Hack(r);
        int idx = r.Frame.StartsWith("film_", StringComparison.Ordinal) ? FrameNo(r.Frame) : 0;
        if (r.FrameNew && idx > 0)
        {
            if (idx == 5) h.AlertAt = r.T;
            if (idx == 7 && r.T - h.AccessAt > 1.2)
            {
                if (h.Layer >= 2) { h.AccessAt = r.T; h.Layer = 0; }
                else
                {
                    h.BreakAt = r.T;
                    h.Layer++;
                    // Осколки красной рамки льда.
                    var c = FP(PA_X + 60, PA_Y + 21 + (h.Layer - 1) * 11);
                    for (int i = 0; i < 10; i++)
                    {
                        double ang = Rnd(0, Math.PI * 2), sp = Rnd(60, 150);
                        var p = Emit(r, P_CONF, c, Math.Cos(ang) * sp, Math.Sin(ang) * sp, 0.5, 2.2, RED);
                        if (p != null) { p.Drag = 3; p.VRot = Rnd(-15, 15); }
                    }
                }
            }
            h.Idx = idx;
        }
        bool access = r.T - h.AccessAt < 1.15;
        // Доля слоя: от film_07 (слой только что снят) до film_06 следующего круга.
        double frac = ((h.Idx - 7 + 8) % 8) / 8.0;
        double target = access ? 1 : (h.Layer + frac) / 3.0;
        h.Shown += (target - h.Shown) * Math.Min(1, fxDt * (target < h.Shown ? 4 : 6));
        h.HexT += fxDt;
        if (h.HexT > 0.11)
        {
            h.HexT = 0;
            for (int i = 0; i < h.Hex.Length - 1; i++) h.Hex[i] = h.Hex[i + 1];
            h.Hex[h.Hex.Length - 1] = HexLine();
        }
        bool alert = r.T - h.AlertAt < 0.5 && !access;
        // Поток символов вверх от голо-экрана (не над лицом — с дальнего края экрана).
        for (int n = Rate(r, 0, alert ? 26 : 18); n > 0; n--)
        {
            var c = alert && rnd.NextDouble() < 0.5 ? RED : access ? AnyOf(GREEN, CYAN) : AnyOf(CYAN, CYAN, PALE);
            var p = Emit(r, P_HEX, FP(Rnd(194, 238), Rnd(94, 102)), VX(Rnd(-6, 14)), -Rnd(42, 72), Rnd(0.8, 1.2), 3, c);
            if (p != null) p.Text = HEXCH[rnd.Next(16)].ToString();
        }
        // Короткие глитч-полосы у панелей; при тревоге — чаще.
        for (int n = Rate(r, 1, alert ? 16 : 3.5); n > 0; n--)
            Emit(r, P_BAR, FP(Rnd(PA_X - 6, PA_X + 112), Rnd(PA_Y - 8, PC_Y + 30)), 0, 0, Rnd(0.05, 0.12), Rnd(10, 42), rnd.NextDouble() < 0.7 ? RED : CYAN);
    }

    // Башня корпорации вдалеке за ней: каркас уступами, этажи, шпиль с маячком; взломанные этажи краснеют снизу
    // вверх, по рёбрам бегут огни взлома; при доступе — вспыхивает зелёно-бирюзовым.
    static readonly double[] TIER_Y = { 300, 222, 168, 134 }, TIER_W = { 36, 28, 20 };
    void HackBack(FxRun r)
    {
        var h = Hack(r);
        bool access = r.T - h.AccessAt < 1.15;
        bool alert = r.T - h.AlertAt < 0.5 && !access;
        double ak = access ? 1 - (r.T - h.AccessAt) / 1.15 : 0;
        const double bx = 50, by = 300, dx = 9, dy = -5;
        Color line = Mix(CYAN, GREEN, ak);
        double baseA = 0.42 + 0.5 * ak;
        // Горизонт и пара дальних домиков.
        Seg(-46, by, 132, by, CYAN, 0.16, 1);
        WireBox(4, by, 14, 36, CYAN, 0.18);
        WireBox(-26, by, 18, 58, CYAN, 0.14);
        double hackY = by - h.Shown * (by - TIER_Y[3]);
        for (int t = 0; t < 3; t++)
        {
            double x0 = bx - TIER_W[t] / 2, x1 = bx + TIER_W[t] / 2, y0 = TIER_Y[t + 1], y1 = TIER_Y[t];
            // Передняя грань, боковая и задняя кромка — каркас в лёгкой перспективе.
            Seg(x0, y1, x0, y0, line, baseA, 1);
            Seg(x1, y1, x1, y0, line, baseA, 1);
            Seg(x0, y0, x1, y0, line, baseA, 1);
            Seg(x0, y0, x0 + dx, y0 + dy, line, baseA * 0.7, 1);
            Seg(x1, y0, x1 + dx, y0 + dy, line, baseA * 0.7, 1);
            Seg(x0 + dx, y0 + dy, x1 + dx, y0 + dy, line, baseA * 0.7, 1);
            Seg(x1 + dx, y0 + dy, x1 + dx, y1 + dy, line, baseA * 0.5, 1);
            Seg(bx, y1, bx, y0, line, 0.14, 1);
            // Этажи: взломанные (ниже границы) — красные (при доступе — зелёные).
            for (double fy = y1 - 6; fy > y0 + 2; fy -= 6)
            {
                bool hacked = fy > hackY;
                Seg(x0, fy, x1, fy, hacked ? (access ? GREEN : RED) : CYAN, hacked ? 0.6 : 0.16, 1);
                Seg(x1, fy, x1 + dx, fy + dy, hacked ? (access ? GREEN : RED) : CYAN, hacked ? 0.3 : 0.08, 1);
            }
        }
        // Граница взлома — яркая черта.
        if (h.Shown > 0.02 && h.Shown < 0.99)
        {
            int t = hackY > TIER_Y[1] ? 0 : hackY > TIER_Y[2] ? 1 : 2;
            Seg(bx - TIER_W[t] / 2 - 2, hackY, bx + TIER_W[t] / 2 + 2, hackY, YELLOW, 0.85, 1.5f);
        }
        // Шпиль и маячок.
        Seg(bx, TIER_Y[3], bx, TIER_Y[3] - 24, line, baseA, 1);
        var beacon = FP(bx, TIER_Y[3] - 24);
        bool blink = ((int)(r.T / 0.6)) % 2 == 0;
        g.FillEllipse(B(Ca(access ? GREEN : RED, 0.25)), beacon.X - 6, beacon.Y - 6, 12, 12);
        g.FillEllipse(B(Ca(access ? GREEN : RED, blink ? 0.95 : 0.4)), beacon.X - 2.5f, beacon.Y - 2.5f, 5, 5);
        if (access)
        {
            float rr = (float)(4 + 34 * (1 - ak));
            g.DrawEllipse(P(Ca(GREEN, ak), 1.5f), beacon.X - rr, beacon.Y - rr, rr * 2, rr * 2);
        }
        // Огни взлома бегут вверх по рёбрам до границы.
        double reach = Math.Max(by - hackY, 20);
        for (int i = 0; i < 6; i++)
        {
            double ly = by - ((r.T * 0.6 + i / 6.0) % 1) * reach;
            int t = ly > TIER_Y[1] ? 0 : ly > TIER_Y[2] ? 1 : 2;
            var lp = FP(i % 2 == 0 ? bx - TIER_W[t] / 2 : bx + TIER_W[t] / 2, ly);
            g.FillEllipse(B(Ca(RED, 0.25)), lp.X - 4, lp.Y - 4, 8, 8);
            g.FillEllipse(B(Ca(alert ? RED : access ? GREEN : YELLOW, 0.95)), lp.X - 1.6f, lp.Y - 1.6f, 3.2f, 3.2f);
        }
    }

    // Дальний домик-каркас: прямоугольник с крышей и парой этажей.
    void WireBox(double x0, double y1, double w, double h, Color c, double a)
    {
        Seg(x0, y1, x0, y1 - h, c, a, 1);
        Seg(x0 + w, y1, x0 + w, y1 - h, c, a, 1);
        Seg(x0, y1 - h, x0 + w, y1 - h, c, a, 1);
        for (double fy = y1 - 8; fy > y1 - h + 3; fy -= 8) Seg(x0, fy, x0 + w, fy, c, a * 0.6, 1);
    }

    void ProjLine(double x0, double y0, double x1, double y1, Color c) { Seg(x0, y0, x1, y1, c, 0.22, 1); }

    void HackFront(FxRun r)
    {
        var h = Hack(r);
        double at = r.T - h.AccessAt;
        bool access = at < 1.15;
        bool alert = r.T - h.AlertAt < 0.5 && !access;
        bool blink = ((int)(r.T / 0.09)) % 2 == 0;
        Color edge = access ? GREEN : alert && blink ? RED : CYAN;
        // Проекция: тонкие лучи от голо-экрана к панелям.
        ProjLine(232, 108, PA_X, PA_Y + 6, edge);
        ProjLine(232, 108, PA_X, PA_Y + 52, edge);
        ProjLine(238, 138, PB_X, PB_Y + 4, edge);
        ProjLine(238, 138, PB_X, PB_Y + 52, edge);

        // Панель А: слои льда (ICE) башни.
        var pa = Panel(Rect(PA_X, PA_Y, 112, 52), edge, r.T / 0.25);
        if (r.T > 0.26)
        {
            g.DrawString(Tr("ЛЁД // БАШНЯ", "ICE // TOWER"), fxTiny, B(Ca(edge, 0.95)), pa.X + 7, pa.Y + 4, tight);
            for (int i = 0; i < 3; i++)
            {
                float ry = pa.Y + 17 + i * 11;
                bool broken = i < h.Layer || access;
                bool active = i == h.Layer && !access;
                var lc = broken ? Ca(access ? GREEN : CYAN, 0.9) : active ? Ca(RED, 1) : Ca(MUTED, 0.7);
                g.DrawString(Tr("СЛОЙ ", "LAYER ") + (i + 1), fxTiny, B(lc), pa.X + 7, ry, tight);
                string state = broken ? Tr("СНЯТ", "DOWN") : active ? Tr("АТАКА", "ATTACK") : Tr("ЗАКРЫТ", "LOCKED");
                if (!(active && !blink && alert)) g.DrawString(state, fxTiny, B(lc), pa.X + 50, ry, tight);
                if (broken) g.DrawLine(P(Ca(access ? GREEN : CYAN, 0.7), 1), pa.X + 6, ry + 4.5f, pa.X + 40, ry + 4.5f);
                if (active)
                {
                    // Красная ICE-рамка вокруг атакуемого слоя, мигает.
                    var ice = new RectangleF(pa.X + 3, ry - 2, pa.Width - 26, 11);
                    if (blink || !alert) g.DrawRectangle(P(Ca(RED, alert ? 1 : 0.75), 1.2f), ice.X, ice.Y, ice.Width, ice.Height);
                    g.FillRectangle(B(Ca(RED, 0.9)), pa.Right - 21, ice.Y, 18, 11);
                    g.DrawString("ICE", fxTiny, B(Ca(INK, 1)), pa.Right - 19.5f, ice.Y + 1, tight);
                }
                // Слой только что снят: рамка разлетается.
                double bt = r.T - h.BreakAt;
                if (i == h.Layer - 1 && bt < 0.4)
                {
                    float grow = (float)(14 * bt / 0.4);
                    g.DrawRectangle(P(Ca(RED, 1 - bt / 0.4), 1.5f), pa.X + 3 - grow, ry - 2 - grow / 2, pa.Width - 26 + 2 * grow, 11 + grow);
                }
            }
        }
        // Тревога: красная ICE-рамка вокруг всей панели и «! ICE» над ней.
        if (alert && r.T > 0.26)
        {
            if (blink) g.DrawRectangle(P(Ca(RED, 1), 1.6f), pa.X - 4, pa.Y - 4, pa.Width + 8, pa.Height + 8);
            g.FillRectangle(B(Ca(RED, 0.95)), pa.Right - 38, pa.Y + 3, 34, 10);
            g.DrawString("! ICE", fxTiny, B(Ca(INK, 1)), pa.Right - 34, pa.Y + 3, tight);
        }

        // Панель Б: бегущий hex-дамп.
        var pb = Panel(Rect(PB_X, PB_Y, 86, 54), edge, (r.T - 0.1) / 0.25);
        if (r.T > 0.36)
        {
            var st = g.Save();
            g.SetClip(pb);
            float shift = (float)(h.HexT / 0.11 * 9.5);
            for (int i = 0; i < h.Hex.Length; i++)
            {
                var c = i == h.Hex.Length - 1 ? YELLOW : i % 2 == 0 ? CYAN : PALE;
                g.DrawString(h.Hex[i], fxMono, B(Ca(c, 0.55 + 0.09 * i)), pb.X + 5, pb.Y + 6 + i * 9.5f - shift, tight);
            }
            g.Restore(st);
        }

        // Шкала «ВЗЛОМ ЛЬДА ▮▮▮▯ NN%».
        var pc = Panel(Rect(PC_X, PC_Y, 116, 30), edge, (r.T - 0.2) / 0.25);
        if (r.T > 0.46)
        {
            var lc = access ? GREEN : alert ? RED : CYAN;
            g.DrawString(Tr("ВЗЛОМ ЛЬДА", "ICE BREACH"), fxTiny, B(Ca(lc, 1)), pc.X + 7, pc.Y + 4, tight);
            string pct = ((int)Math.Round(h.Shown * 100)).ToString() + "%";
            var psz = Measure(pct, fxSmall);
            g.DrawString(pct, fxSmall, B(Ca(YELLOW, 1)), pc.Right - 7 - psz.Width, pc.Y + 3, tight);
            float segW = (pc.Width - 14 - 9 * 2.5f) / 10;
            double lit = h.Shown * 10;
            for (int i = 0; i < 10; i++)
            {
                float px = pc.X + 7 + i * (segW + 2.5f), py = pc.Y + 17;
                bool on = i < (int)lit;
                bool part = i == (int)lit && lit - i > 0.15;
                var sc = on ? Ca(lc, 1) : part ? Ca(lc, ((int)(r.T / 0.2)) % 2 == 0 ? 0.85 : 0.3) : Ca(Color.FromArgb(40, 60, 70), 0.8);
                q4[0] = new PointF(px + 2, py); q4[1] = new PointF(px + segW + 2, py); q4[2] = new PointF(px + segW, py + 7); q4[3] = new PointF(px, py + 7);
                g.FillPolygon(B(sc), q4);
            }
        }

        if (access) AccessFlash(at);
    }

    // «ДОСТУП ОТКРЫТ»: кольцо вспышки от голо-экрана и плашка с надписью, первые мгновения — с цветным сдвигом.
    void AccessFlash(double at)
    {
        if (at < 0.45)
        {
            var c = FP(SCR_X, SCR_Y);
            float rr = (float)(10 + 150 * Ease(at / 0.45));
            g.DrawEllipse(P(Ca(GREEN, 1 - at / 0.45), 2.5f), c.X - rr, c.Y - rr, rr * 2, rr * 2);
        }
        string T = Tr("ДОСТУП ОТКРЫТ", "ACCESS GRANTED");
        var sz = Measure(T, fxBig);
        var pc = FP(PA_X + 50, PA_Y + 46);
        float s = at < 0.15 ? (float)(1.35 - 0.35 * at / 0.15) : 1;
        double fade = at > 0.9 ? Math.Max(0, 1 - (at - 0.9) / 0.25) : 1;
        var st = g.Save();
        g.TranslateTransform(pc.X, pc.Y);
        g.ScaleTransform(s, s);
        var plate = new RectangleF(-sz.Width / 2 - 22, -sz.Height / 2 - 6, sz.Width + 34, sz.Height + 12);
        for (int i = 3; i >= 1; i--) g.DrawRectangle(P(Ca(GREEN, 0.14 * fade), i * 3f), plate.X, plate.Y, plate.Width, plate.Height);
        g.FillRectangle(B(Ca(INK, 0.9 * fade)), plate);
        g.DrawRectangle(P(Ca(GREEN, fade), 2), plate.X, plate.Y, plate.Width, plate.Height);
        // Галочка.
        q3[0] = new PointF(plate.X + 7, 0); q3[1] = new PointF(plate.X + 11, 4); q3[2] = new PointF(plate.X + 18, -5);
        g.DrawLines(P(Ca(GREEN, fade), 2.2f), q3);
        float tx = -sz.Width / 2 + 9, ty = -sz.Height / 2;
        if (at < 0.2)
        {
            g.DrawString(T, fxBig, B(Ca(RED, 0.6 * fade)), tx - 2, ty, tight);
            g.DrawString(T, fxBig, B(Ca(CYAN, 0.6 * fade)), tx + 2, ty, tight);
        }
        g.DrawString(T, fxBig, B(Ca(CYAN, fade)), tx, ty, tight);
        g.Restore(st);
    }

    // ---- Талия: голо-печать, сюрикен-искры, красные глитчи ----
    void SealTick(FxRun r)
    {
        if (!r.FrameNew || r.Frame != "selfie_02") return;
        r.Mark = r.T;
        var c = SealPoint();
        for (int i = 0; i < 8; i++)
        {
            double ang = Rnd(0, Math.PI * 2), sp = Rnd(60, 150);
            var p = Emit(r, P_SHURI, c, Math.Cos(ang) * sp, Math.Sin(ang) * sp, 0.6, 3.5, AnyOf(RED, PALE));
            if (p != null) { p.Drag = 2.5; p.VRot = Rnd(-14, 14); }
        }
    }

    PointF SealPoint() { return FP(aHeadX - 118, aTop + 88); }

    // Круглая голо-печать: два кольца с рисками, две встречно вращающиеся треугольные звезды, ромб в центре.
    void SealFront(FxRun r)
    {
        double t = r.T - r.Mark;
        if (r.Mark < 0 || t < 0) return;
        var c = SealPoint();
        float R = (float)(34 * Ease(t / 0.3) * fxKx);
        if (R < 1) return;
        double rot = r.T * 0.9;
        g.FillEllipse(B(Ca(RED, 0.12)), c.X - R * 1.25f, c.Y - R * 1.25f, R * 2.5f, R * 2.5f);
        g.DrawEllipse(P(Ca(RED, 0.3), 4f), c.X - R, c.Y - R, R * 2, R * 2);
        g.DrawEllipse(P(Ca(RED, 0.95), 1.8f), c.X - R, c.Y - R, R * 2, R * 2);
        g.DrawEllipse(P(Ca(CYAN, 0.8), 1.1f), c.X - R * 0.78f, c.Y - R * 0.78f, R * 1.56f, R * 1.56f);
        for (int i = 0; i < 24; i++)
        {
            double ang = rot + i * Math.PI / 12;
            g.DrawLine(P(Ca(i % 3 == 0 ? YELLOW : RED, 0.85), 1), Polar(c.X, c.Y, R * 0.82, ang), Polar(c.X, c.Y, R * (i % 3 == 0 ? 0.97 : 0.9), ang));
        }
        for (int k = 0; k < 2; k++)
        {
            double a0 = -rot * 1.4 + k * Math.PI;
            for (int i = 0; i < 3; i++) q3[i] = Polar(c.X, c.Y, R * 0.7, a0 + i * 2 * Math.PI / 3);
            g.DrawPolygon(P(Ca(k == 0 ? RED : CYAN, 0.9), 1.3f), q3);
        }
        float d = R * 0.16f;
        q4[0] = new PointF(c.X, c.Y - d); q4[1] = new PointF(c.X + d, c.Y); q4[2] = new PointF(c.X, c.Y + d); q4[3] = new PointF(c.X - d, c.Y);
        g.FillPolygon(B(Ca(CYAN, 1)), q4);
    }

    static readonly Color[] SHURI = { RED, PALE, RED };
    void ShurikenTick(FxRun r) { Twinkles(r, P_SHURI, SHURI); }

    void ShurikenBurstTick(FxRun r)
    {
        if (r.T > 0.3) return;
        for (int n = Rate(r, 0, 45); n > 0; n--)
        {
            double side = Side();
            var p = Emit(r, rnd.NextDouble() < 0.7 ? P_SHURI : P_SPARK, FP(aHeadX + side * Rnd(22, 46), aTop + Rnd(10, 50)), VX(side * Rnd(80, 200)), -Rnd(60, 200),
                Rnd(0.8, 1.1), Rnd(3.5, 5.5), AnyOf(RED, PALE, RED));
            if (p == null) return;
            p.Grav = 260;
            p.Drag = 1.2;
            p.VRot = Rnd(-14, 14);
        }
    }

    // Красные глитч-полосы вокруг фигуры (мимо лица).
    void GlitchTick(FxRun r)
    {
        for (int n = Rate(r, 3, r.T < 0.6 ? 14 : 4); n > 0; n--)
        {
            double fx = aHeadX + Rnd(-95, 95), fy = aTop + Rnd(10, 220);
            if (Math.Abs(fx - aHeadX) < 42 && fy < aTop + 76) continue;
            Emit(r, P_BAR, FP(fx, fy), 0, 0, Rnd(0.05, 0.12), Rnd(10, 40), rnd.NextDouble() < 0.75 ? RED : CYAN);
        }
    }

    // ---- Ди, механик: чинит парящего дрона ----
    sealed class RepairState { public double NextBolt = 0.6, NextPuff = 2.5, PuffAt = -10; }

    // Рабочая точка: дрон в руках у груди (кадры film у Ди), ниже лица.
    PointF WorkPoint() { return FP(aHeadX - 18, aTop + 76); }

    void RepairTick(FxRun r)
    {
        var s = (RepairState)(r.Data ?? (r.Data = new RepairState()));
        var w = WorkPoint();
        if (r.T % 1.0 < 0.62)
            for (int n = Rate(r, 0, 38); n > 0; n--)
            {
                // Искры сварки — вбок и вниз, мимо лица.
                double ang = Rnd(-0.35, Math.PI + 0.35), sp = Rnd(90, 230);
                var p = Emit(r, P_STREAK, w, Math.Cos(ang) * sp, Math.Sin(ang) * sp, Rnd(0.25, 0.5), 1.4, AnyOf(YELLOW, PALE, Color.FromArgb(255, 200, 80)));
                if (p != null) p.Grav = 520;
            }
        if (r.T > s.NextBolt)
        {
            s.NextBolt = r.T + Rnd(0.8, 1.5);
            var p = Emit(r, rnd.Next(2) == 0 ? P_GEAR : P_BOLT, w, VX(-Rnd(60, 140)), -Rnd(110, 170), 1.3, Rnd(3.5, 5.5), AnyOf(PALE, Color.FromArgb(170, 180, 195), YELLOW));
            if (p != null) { p.Grav = 460; p.VRot = Rnd(-9, 9); }
        }
        if (r.T > s.NextPuff)
        {
            s.NextPuff = r.T + Rnd(3, 4.5);
            s.PuffAt = r.T;
            for (int i = 0; i < 6; i++)
            {
                var p = Emit(r, P_PUFF, new PointF(w.X + (float)Rnd(-6, 6), w.Y + (float)Rnd(-4, 4)), VX(-Rnd(20, 60)), -Rnd(10, 35), 0.9, Rnd(5, 8), SMOKE);
                if (p != null) { p.Drag = 2; p.Grow = 22; }
            }
        }
    }

    void RepairFront(FxRun r)
    {
        var s = r.Data as RepairState;
        if (s == null) return;
        // Голо-чертёж сбоку: сетка, дрон сверху, размерные линии.
        var bp = Panel(Rect(aL - 118, aTop + 30, 100, 72), CYAN, r.T / 0.25);
        if (r.T > 0.26)
        {
            var grid = P(Ca(CYAN, 0.12), 1);
            for (float gx = bp.X + 10; gx < bp.Right; gx += 10) g.DrawLine(grid, gx, bp.Y + 1, gx, bp.Bottom - 1);
            for (float gy = bp.Y + 10; gy < bp.Bottom; gy += 10) g.DrawLine(grid, bp.X + 1, gy, bp.Right - 1, gy);
            g.DrawString(Tr("ЧЕРТЁЖ // ДРОН-7", "BLUEPRINT // DRONE-7"), fxTiny, B(Ca(CYAN, 0.95)), bp.X + 6, bp.Y + 4, tight);
            float cx0 = bp.X + bp.Width * 0.44f, cy0 = bp.Y + bp.Height * 0.58f;
            var pen = P(Ca(PALE, 0.85), 1.1f);
            g.DrawEllipse(pen, cx0 - 8, cy0 - 6, 16, 12);
            for (int i = 0; i < 4; i++)
            {
                float ox = (i % 2 == 0 ? -1 : 1) * 19, oy = (i < 2 ? -1 : 1) * 13;
                g.DrawLine(pen, cx0 + ox * 0.35f, cy0 + oy * 0.35f, cx0 + ox, cy0 + oy);
                g.DrawEllipse(P(Ca(CYAN, 0.8), 1), cx0 + ox - 7, cy0 + oy - 4, 14, 8);
                // Вращение винта — бегущая черта.
                double ang = r.T * 14 + i;
                g.DrawLine(P(Ca(PALE, 0.7), 1), Polar(cx0 + ox, cy0 + oy, 6, ang), Polar(cx0 + ox, cy0 + oy, 6, ang + Math.PI));
            }
            // Размерная линия снизу и метки.
            float ly = bp.Bottom - 7;
            g.DrawLine(P(Ca(YELLOW, 0.8), 1), cx0 - 26, ly, cx0 + 26, ly);
            g.DrawLine(P(Ca(YELLOW, 0.8), 1), cx0 - 26, ly - 3, cx0 - 26, ly + 3);
            g.DrawLine(P(Ca(YELLOW, 0.8), 1), cx0 + 26, ly - 3, cx0 + 26, ly + 3);
            g.DrawString("Ø42", fxTiny, B(Ca(YELLOW, 0.9)), bp.Right - 24, bp.Bottom - 13, tight);
            g.DrawString("R-07", fxTiny, B(Ca(PALE, 0.8)), bp.Right - 24, bp.Y + 16, tight);
        }
        // Вспышка сварки в рабочей точке.
        if (r.T % 1.0 < 0.62)
        {
            var w = WorkPoint();
            float fl = (float)Rnd(5, 11);
            g.FillEllipse(B(Ca(CYAN, 0.25)), w.X - fl * 1.8f, w.Y - fl * 1.8f, fl * 3.6f, fl * 3.6f);
            g.FillEllipse(B(Ca(Color.White, 0.9)), w.X - fl / 2, w.Y - fl / 2, fl, fl);
        }
        // Дрон-светлячок: кружит сбоку от героя, светится, оставляет след.
        for (int i = 4; i >= 0; i--)
        {
            var c = FireflyPos(r.T - i * 0.05);
            if (i > 0) { g.FillEllipse(B(Ca(YELLOW, 0.18 * (5 - i) / 5)), c.X - 2, c.Y - 2, 4, 4); continue; }
            g.FillEllipse(B(Ca(YELLOW, 0.22)), c.X - 9, c.Y - 9, 18, 18);
            g.FillEllipse(B(Ca(INK, 0.95)), c.X - 6, c.Y - 2.5f, 12, 5);
            g.DrawEllipse(P(Ca(CYAN, 0.9), 1), c.X - 6, c.Y - 2.5f, 12, 5);
            bool on = ((int)(r.T / 0.05)) % 2 == 0;
            g.DrawLine(P(Ca(PALE, on ? 0.8 : 0.4), 1), c.X - 10, c.Y - 4, c.X - 4, c.Y - 4);
            g.DrawLine(P(Ca(PALE, on ? 0.4 : 0.8), 1), c.X + 4, c.Y - 4, c.X + 10, c.Y - 4);
            g.FillEllipse(B(Ca(YELLOW, 1)), c.X - 1.5f, c.Y - 1.5f, 3, 3);
        }
        // «пых!» рядом с облачком дыма.
        double pt = r.T - s.PuffAt;
        if (pt < 1.0)
        {
            // Подпись — снаружи, в сторону от лица.
            var w = FP(aHeadX - 74, aTop + 70);
            Outlined(Tr("пых!", "puff!"), fxSmall, w.X - 10, w.Y - (float)(pt * 16), PALE, fxA * (1 - pt), INK);
        }
    }

    PointF FireflyPos(double t)
    {
        double ang = t * 2.2;
        return FP(aHeadX + 104 + Math.Cos(ang) * 22, aTop + 18 + Math.Sin(ang) * 12 + Math.Sin(t * 5) * 3);
    }

    void GearBurstTick(FxRun r)
    {
        if (r.T > 0.3) return;
        for (int n = Rate(r, 1, 30); n > 0; n--)
        {
            double side = Side();
            var p = Emit(r, rnd.Next(2) == 0 ? P_GEAR : P_BOLT, FP(aHeadX + side * Rnd(24, 50), aTop + Rnd(10, 40)), VX(side * Rnd(50, 160)), -Rnd(140, 240),
                Rnd(1.0, 1.4), Rnd(3.5, 5.5), AnyOf(PALE, YELLOW, CYAN));
            if (p == null) return;
            p.Grav = 420;
            p.VRot = Rnd(-10, 10);
        }
    }

    // ---- Уик, фиксер: тактическая голо-карта из очков ----
    static readonly double[][] BLOCKS =
    {
        new[] { 0.06, 0.1, 0.22, 0.38 }, new[] { 0.28, 0.08, 0.46, 0.3 }, new[] { 0.62, 0.52, 0.9, 0.86 },
        new[] { 0.1, 0.55, 0.3, 0.9 }, new[] { 0.5, 0.12, 0.66, 0.4 },
    };

    void MapFront(FxRun r)
    {
        double mx0 = aHeadX + 56, my0 = aTop + 66, mw = 128, mh = 78;
        var a = FP(mx0 + 16, my0);
        var b = FP(mx0 + mw + 16, my0);
        var c = FP(mx0 + mw, my0 + mh);
        var d = FP(mx0, my0 + mh);
        double open = Ease(r.T / 0.35);
        if (open <= 0.01) return;
        Func<double, double, PointF> M = (u, v) =>
        {
            v *= open;
            float tx = a.X + (b.X - a.X) * (float)u, ty = a.Y + (b.Y - a.Y) * (float)u;
            float bx = d.X + (c.X - d.X) * (float)u, by = d.Y + (c.Y - d.Y) * (float)u;
            return new PointF(tx + (bx - tx) * (float)v, ty + (by - ty) * (float)v);
        };
        q4[0] = M(0, 0); q4[1] = M(1, 0); q4[2] = M(1, 1); q4[3] = M(0, 1);
        g.FillPolygon(B(Ca(INK, 0.45)), q4);
        g.FillPolygon(B(Ca(RED, 0.08)), q4);
        // Кварталы и сетка.
        foreach (var bl in BLOCKS)
        {
            q4[0] = M(bl[0], bl[1]); q4[1] = M(bl[2], bl[1]); q4[2] = M(bl[2], bl[3]); q4[3] = M(bl[0], bl[3]);
            g.FillPolygon(B(Ca(RED, 0.16)), q4);
        }
        var grid = P(Ca(RED, 0.22), 1);
        for (int i = 1; i < 8; i++) g.DrawLine(grid, M(i / 8.0, 0), M(i / 8.0, 1));
        for (int i = 1; i < 5; i++) g.DrawLine(grid, M(0, i / 5.0), M(1, i / 5.0));
        q4[0] = M(0, 0); q4[1] = M(1, 0); q4[2] = M(1, 1); q4[3] = M(0, 1);
        g.DrawPolygon(P(Ca(RED, 0.25), 3.5f), q4);
        g.DrawPolygon(P(Ca(RED, 0.95), 1.3f), q4);
        if (open < 0.98) return;
        // Маршрут пунктиром — бежит к цели.
        fxDash.Color = Ca(YELLOW, 0.85);
        fxDash.Width = 1.4f;
        fxDash.DashOffset = (float)(-r.T * 6);
        g.DrawLines(fxDash, new[] { M(0.08, 0.86), M(0.34, 0.62), M(0.55, 0.64), M(0.78, 0.3) });
        fxDash.Color = Ca(RED, 0.6);
        g.DrawLines(fxDash, new[] { M(0.92, 0.9), M(0.7, 0.72), M(0.3, 0.26) });
        // Метки целей.
        bool blink = ((int)(r.T / 0.35)) % 2 == 0;
        var t1 = M(0.78, 0.3);
        float s = 5;
        q4[0] = new PointF(t1.X, t1.Y - s); q4[1] = new PointF(t1.X + s, t1.Y); q4[2] = new PointF(t1.X, t1.Y + s); q4[3] = new PointF(t1.X - s, t1.Y);
        g.DrawPolygon(P(Ca(blink ? YELLOW : RED, 1), 1.5f), q4);
        g.DrawLine(P(Ca(RED, 0.9), 1), t1.X - 9, t1.Y, t1.X + 9, t1.Y);
        g.DrawLine(P(Ca(RED, 0.9), 1), t1.X, t1.Y - 9, t1.X, t1.Y + 9);
        if (blink) Outlined(Tr("ЦЕЛЬ", "TARGET"), fxTiny, t1.X + 7, t1.Y - 12, YELLOW, fxA, INK);
        var t2 = M(0.3, 0.26);
        g.DrawEllipse(P(Ca(RED, 0.9), 1.2f), t2.X - 3.5f, t2.Y - 3.5f, 7, 7);
        // «КОНТРАКТ» — плашка над картой, мигает медленнее.
        var head = M(0, 0);
        if (((int)(r.T / 0.7)) % 2 == 0 || r.T < 0.7)
        {
            g.FillRectangle(B(Ca(RED, 0.9)), head.X, head.Y - 13, 56, 11);
            g.DrawString(Tr("КОНТРАКТ", "CONTRACT"), fxTiny, B(Ca(INK, 1)), head.X + 4, head.Y - 13, tight);
        }
        // Сканирующий луч из очков: веер к бегущей по карте черте.
        double us = (r.T * 0.45) % 1;
        var s0 = M(us, 0);
        var s1 = M(us, 1);
        g.DrawLine(P(Ca(RED, 0.35), 4f), s0, s1);
        g.DrawLine(P(Ca(Color.FromArgb(255, 170, 170), 1), 1.2f), s0, s1);
        var eye = FP(aHeadX + 10, aTop + 36);
        var mid = new PointF((s0.X + s1.X) / 2, (s0.Y + s1.Y) / 2);
        double len = Math.Sqrt((mid.X - eye.X) * (mid.X - eye.X) + (mid.Y - eye.Y) * (mid.Y - eye.Y));
        if (len > 1)
        {
            // Луч начинается чуть в стороне от очков — лицо не закрываем.
            var e = new PointF(eye.X + (float)((mid.X - eye.X) / len * 14), eye.Y + (float)((mid.Y - eye.Y) / len * 14));
            q3[0] = e; q3[1] = s0; q3[2] = s1;
            g.FillPolygon(B(Ca(RED, 0.07)), q3);
            g.DrawLine(P(Ca(RED, 0.35), 1), e, s0);
            g.DrawLine(P(Ca(RED, 0.35), 1), e, s1);
        }
    }

    // Монета: подброшена из руки, крутится, падает обратно в руку; в верхней точке — блик.
    void CoinFront(FxRun r)
    {
        const double fly = 0.95;
        double cyc = r.T % 1.5;
        var hand = FP(aHeadX + 50, aTop + 118);
        double up = cyc < fly ? 4 * 120 * (cyc / fly) * (1 - cyc / fly) : 0;
        float y = hand.Y - (float)(up * fxKy);
        double spin = cyc < fly ? cyc * 26 : 0;
        float rw = (float)(7 * Math.Abs(Math.Cos(spin))) + 1, rh = 7;
        g.FillEllipse(B(Ca(Color.FromArgb(150, 120, 0), 1)), hand.X - rw, y - rh + 1, 2 * rw, 2 * rh);
        g.FillEllipse(B(Ca(YELLOW, 1)), hand.X - rw + 0.8f, y - rh, Math.Max(0.5f, 2 * rw - 1.6f), 2 * rh - 1);
        if (rw > 4) g.DrawEllipse(P(Ca(Color.FromArgb(180, 150, 0), 0.9), 1), hand.X - rw + 3, y - rh + 3, 2 * rw - 6, 2 * rh - 7);
        if (Math.Abs(cyc - fly / 2) < 0.12) Spark(hand.X + 4, y - 4, 4, PALE, fxA * (1 - Math.Abs(cyc - fly / 2) / 0.12));
        if (cyc >= fly && cyc < fly + 0.15) Spark(hand.X, hand.Y - 4, 3, YELLOW, fxA * (1 - (cyc - fly) / 0.15));
    }

    // Скучает — дымок вьётся вверх.
    void SmokeTick(FxRun r)
    {
        for (int n = Rate(r, 0, 3.5); n > 0; n--)
        {
            var p = Emit(r, P_PUFF, FP(aHeadX + 48, aTop + 116), VX(Rnd(-6, 10)), -Rnd(22, 34), 2.0, 2.5, SMOKE);
            if (p != null) { p.Wob = 3; p.Grow = 9; p.Drag = 0.3; }
        }
    }

    // ==== напарник в кадре: пока герой снимает (film, an), рядом трюкачит второй герой из state.json (costar) ====
    // Трюки — по очереди (иногда трюк повторяется); прыжки нарисованы в кадрах, перелёт вбок — здесь: на кадрах
    // полёта (ступни выше земли) напарник смещается к другой площадке, следующий прыжок — обратно.
    sealed class Costar
    {
        public string Sprite, Name, Folder, Key;
        public List<List<Step>> Stunts = new List<List<Step>>();
    }
    const float CO_SCALE = 0.8f;
    float CoK { get { return CO_SCALE * sizePct / 100f; } }
    // Настил лесов над полом, расстояние между площадками, отступ ближней площадки от героя.
    const int CO_DECK = 46, CO_GAP = 125, CO_NEAR = 172;
    Costar costar;
    FrameSet coSet;
    FrameData coShown;
    double coK, coX = double.NaN, coFrom, coTo, coNear, coFar;
    int coStunt = -1, coRep, coFace = 1, coExt;
    DateTime coStart;
    RectangleF coRectW;
    bool coRectSet;
    readonly Dictionary<List<Step>, bool[]> coAir = new Dictionary<List<Step>, bool[]>();
    readonly ImageAttributes coAttr = new ImageAttributes();

    void SetCostar(Costar c)
    {
        if (c == null) { costar = null; coSet = null; return; }
        if (costar != null && costar.Key == c.Key) return;
        var set = OpenSet(c.Folder);
        // Кадров трюков могло не быть при прошлом взгляде на папку — раз в 10 с смотрим заново.
        if (set != null && (DateTime.Now - set.Scanned).TotalSeconds > 10 && !AllFrames(set, c)) Scan(set);
        var ok = new List<List<Step>>();
        if (set != null)
            foreach (var st in c.Stunts)
            {
                bool all = true;
                foreach (var s in st) if (!set.Names.Contains(Base(s.F))) { all = false; break; }
                if (all) ok.Add(st);
            }
        c.Stunts = ok;
        // Кадров трюков нет — напарника не рисуем (но в следующий раз проверим снова).
        if (ok.Count == 0) { costar = null; coSet = null; return; }
        costar = c;
        coSet = set;
        coStunt = -1;
        coAir.Clear();
        foreach (var st in ok) WantSteps(set, st);
    }

    static bool AllFrames(FrameSet set, Costar c)
    {
        foreach (var st in c.Stunts) foreach (var s in st) if (!set.Names.Contains(Base(s.F))) return false;
        return true;
    }

    bool CostarWanted(string clip, bool menu, bool walking)
    {
        return costar != null && coSet != null && cur != null && !menu && !walking && (clip == "film" || clip == "an" || (over != null && !overEnter && overOnFilm))
            && !string.Equals(coSet.Folder, cur.Folder, StringComparison.OrdinalIgnoreCase);
    }

    static double Total(List<Step> st) { double t = 0; foreach (var s in st) t += s.Ms; return t; }

    // Какие шаги трюка — в полёте (ступни заметно выше земли). Пока кадры грузятся — средняя треть трюка.
    bool[] AirFlags(List<Step> st)
    {
        bool[] f;
        if (coAir.TryGetValue(st, out f)) return f;
        f = new bool[st.Count];
        bool all = true;
        for (int i = 0; i < st.Count; i++)
        {
            var d = coSet.Get(Base(st[i].F));
            if (d == null) { all = false; break; }
            f[i] = d.Box.Bottom < FRAME_G - 10;
        }
        if (!all)
        {
            for (int i = 0; i < st.Count; i++) f[i] = i >= st.Count / 3 && i < st.Count * 2 / 3;
            return f;
        }
        coAir[st] = f;
        return f;
    }

    // Доля перелёта к моменту t: сколько времени полёта уже прошло.
    double AirFraction(List<Step> st, double t)
    {
        var air = AirFlags(st);
        double total = 0, done = 0, acc = 0;
        for (int i = 0; i < st.Count; i++)
        {
            if (air[i])
            {
                total += st[i].Ms;
                if (t > acc) done += Math.Min(st[i].Ms, t - acc);
            }
            acc += st[i].Ms;
        }
        return total > 0 ? done / total : 0;
    }

    void UpdateCostar(double dt, bool want, int hx)
    {
        coK = Math.Max(0, Math.Min(1, coK + (want ? dt : -dt) / 0.25));
        if (coK <= 0 || costar == null || coSet == null) { coK = costar == null ? 0 : coK; coStunt = -1; coX = double.NaN; coExt = 0; coRectSet = false; return; }
        // Площадки слева от героя — в пределах окна DeepSeek Harness и экрана.
        double sizeK = sizePct / 100.0;
        int limit = Math.Max(boxL == int.MinValue ? scrLeft : boxL, scrLeft) + (int)(70 * sizeK);
        coNear = hx - CO_NEAR * sizeK;
        coFar = coNear - CO_GAP * sizeK;
        if (coFar < limit)
        {
            double shift = limit - coFar;
            coFar += shift;
            coNear = Math.Max(coFar, Math.Min(hx - 110 * sizeK, coNear + shift));
        }
        coExt = Math.Max(0, Math.Min(CW_MAX - W, (int)(hx - W / 2 - (coFar - 100 * sizeK))));
        if (double.IsNaN(coX)) coX = coNear;
        double t = (DateTime.Now - coStart).TotalMilliseconds;
        if (coStunt < 0 || coStunt >= costar.Stunts.Count || t >= Total(costar.Stunts[coStunt]))
        {
            if (coStunt >= 0 && coStunt < costar.Stunts.Count && coRep > 0) coRep--;
            else
            {
                coStunt = (coStunt + 1) % costar.Stunts.Count;
                coRep = rnd.NextDouble() < 0.35 ? 1 : 0;
            }
            coStart = DateTime.Now;
            t = 0;
            coFrom = coX;
            var air = AirFlags(costar.Stunts[coStunt]);
            bool flies = Array.IndexOf(air, true) >= 0;
            coTo = !flies ? coFrom : Math.Abs(coFrom - coNear) < Math.Abs(coFrom - coFar) ? coFar : coNear;
            coFace = coTo < coFrom - 1 ? -1 : 1;
        }
        coX = coFrom + (coTo - coFrom) * Smooth(AirFraction(costar.Stunts[coStunt], t));
    }

    void DrawCostar(int left, double dt)
    {
        if (coK <= 0 || costar == null || coSet == null || coStunt < 0) return;
        double a = Ease(coK);
        float yDeck = GROUND - CO_DECK * sizePct / 100f, yFloor = GROUND - 6;
        Scaffold((float)(coNear - left), yDeck, yFloor, a, 0);
        Scaffold((float)(coFar - left), yDeck, yFloor, a, 1);
        var st = costar.Stunts[coStunt];
        double t = (DateTime.Now - coStart).TotalMilliseconds;
        string name = st[st.Count - 1].F;
        for (int i = 0; i < st.Count; i++) { if (t < st[i].Ms) { name = st[i].F; break; } t -= st[i].Ms; }
        name = Base(name);
        var d = coSet.Get(name);
        if (d == null) { Want(coSet, name, true); d = coShown; }
        else coShown = d;
        if (d == null) return;
        float cx = (float)(coX - left);
        double lift = Math.Max(0, FRAME_G - d.Box.Bottom) / 90.0;
        float shW = (float)(70 * (1 - 0.45 * Math.Min(1, lift)));
        g.FillEllipse(B(Al(Color.Black, 0.22 * a * (1 - 0.5 * Math.Min(1, lift)))), cx - shW / 2, yDeck - 4, shW, 7);
        var state = g.Save();
        g.TranslateTransform(cx, yDeck);
        g.ScaleTransform(coFace * CoK, CoK);
        ImageAttributes ia = null;
        if (a < 0.99)
        {
            coAttr.SetColorMatrix(new ColorMatrix { Matrix33 = (float)a });
            ia = coAttr;
        }
        DrawPart(g, d, new RectangleF(0, 0, d.FW, d.FH), new RectangleF(-d.FW / 2f, -FRAME_G, d.FW, d.FH), ia);
        g.Restore(state);
        // Рамка напарника на холсте (сглаженно) — видоискатель Романа наводится на неё.
        float x0 = cx + coFace * (d.Box.Left - d.FW / 2f) * CoK, x1 = cx + coFace * (d.Box.Right - d.FW / 2f) * CoK;
        var box = RectangleF.FromLTRB(Math.Min(x0, x1) + left, yDeck + (d.Box.Top - FRAME_G) * CoK, Math.Max(x0, x1) + left, yDeck + (d.Box.Bottom - FRAME_G) * CoK);
        if (!coRectSet) { coRectW = box; coRectSet = true; }
        else
        {
            float k = (float)Math.Min(1, dt * 10);
            coRectW = RectangleF.FromLTRB(coRectW.Left + (box.Left - coRectW.Left) * k, coRectW.Top + (box.Top - coRectW.Top) * k,
                coRectW.Right + (box.Right - coRectW.Right) * k, coRectW.Bottom + (box.Bottom - coRectW.Bottom) * k);
        }
    }

    // Неоновые строительные леса: настил, стойки, раскосы крестом; тонкие бирюзовые и красные линии, полупрозрачно.
    void Scaffold(float cx, float yd, float yf, double a, int v)
    {
        float hw = 0.5f * sizePct, dx = 0.09f * sizePct, dy = -0.06f * sizePct;
        double fl = 0.85 + 0.15 * Math.Sin(Environment.TickCount / 1000.0 * (17 + v * 6) + v);
        Color cy = Al(CYAN, a * 0.75 * fl), cyd = Al(CYAN, a * 0.32 * fl), rd = Al(RED, a * 0.55 * fl);
        q4[0] = new PointF(cx - hw, yd); q4[1] = new PointF(cx + hw, yd); q4[2] = new PointF(cx + hw + dx, yd + dy); q4[3] = new PointF(cx - hw + dx, yd + dy);
        g.FillPolygon(B(Al(CYAN, a * 0.12)), q4);
        g.DrawLine(P(Al(CYAN, a * 0.22 * fl), 4), cx - hw, yd, cx + hw, yd);
        g.DrawLine(P(cy, 1.4f), cx - hw, yd, cx + hw, yd);
        g.DrawLine(P(cyd, 1), cx - hw + dx, yd + dy, cx + hw + dx, yd + dy);
        g.DrawLine(P(cyd, 1), cx - hw, yd, cx - hw + dx, yd + dy);
        g.DrawLine(P(cyd, 1), cx + hw, yd, cx + hw + dx, yd + dy);
        // Красная кромка настила — «осторожно».
        for (float px = cx - hw + 2; px < cx + hw - 4; px += 10) g.DrawLine(P(rd, 1.6f), px, yd + 2.5f, px + 5, yd + 2.5f);
        float[] poles = { cx - hw + 3, cx, cx + hw - 3 };
        foreach (var px in poles)
        {
            g.DrawLine(P(cy, 1.2f), px, yd, px, yf);
            g.DrawLine(P(cyd, 1), px + dx, yd + dy, px + dx, yf + dy);
            g.FillEllipse(B(Al(CYAN, a * 0.25)), px - 4, yf - 1.5f, 8, 3);
        }
        for (int i = 0; i < 2; i++)
        {
            g.DrawLine(P(rd, 1), poles[i], yd + 4, poles[i + 1], yf);
            g.DrawLine(P(rd, 1), poles[i + 1], yd + 4, poles[i], yf);
        }
        float ym = (yd + yf) / 2;
        g.DrawLine(P(cyd, 1), poles[0], ym, poles[2], ym);
    }

    // ---- меню выбора напарника (ПКМ) ----
    const int CARD_W = 196, CARD_H = 322, GAP = 14, PAD = 22, HEAD = 62, FOOT = 46, PORT_H = 150;
    const double MENU_LIFE = 20, OPEN_MS = 250, CLOSE_MS = 150;
    const int SET_W = 470, SET_ROW = 36, SET_ROWS = 10;
    const string GLYPHS = "#%&*+<>/\\|=01ЖЯЩЮ";
    bool menuOpen, menuSettings;
    DateTime menuAt, menuTouch, menuClosedAt = DateTime.MinValue;
    RectangleF gearRect;
    // Страницы по PER_PAGE героев, не меньше MIN_PAGES: свободные места — закрытые слоты «скоро».
    const int PER_PAGE = 4, MIN_PAGES = 3;
    int menuPage, menuReal;
    DateTime pageAt;
    RectangleF prevRect, nextRect, updRect;
    readonly List<KeyValuePair<RectangleF, Action>> setHits = new List<KeyValuePair<RectangleF, Action>>();
    List<Hero> menuHeroes = new List<Hero>();
    int menuLeft, cw = W, debugHover = -1;
    Rectangle panel;
    RectangleF[] cardRects = new RectangleF[0];
    float[] hoverK = new float[0];
    Point mouse = new Point(-1, -1);

    public bool MenuOpen { get { return menuOpen; } }
    bool MenuShown { get { return menuOpen || (DateTime.Now - menuClosedAt).TotalMilliseconds < CLOSE_MS; } }

    public void OpenMenu() { OpenMenu(-1); }

    // Открыть меню; hover — подсветить карточку без мыши (для проверки снимком).
    public void OpenMenu(int hover)
    {
        if (cur == null || !hasArea || !Visible) return;
        DropMenu();
        foreach (var h in roster) menuHeroes.Add(h.Copy());
        // Мод не прислал список — в меню один текущий герой.
        if (menuHeroes.Count == 0)
            menuHeroes.Add(new Hero { Id = HeroKey, Sprite = HeroKey, Folder = dir, Name = string.IsNullOrEmpty(HeroName) ? Path.GetFileName(dir) : HeroName, Role = HeroRole, About = "" });
        // Кадры героев из меню — заранее, в фоне: выбор сработает мгновенно.
        foreach (var h in menuHeroes) Prepare(h);
        menuReal = menuHeroes.Count;
        int pages = Math.Max(MIN_PAGES, (menuReal + PER_PAGE - 1) / PER_PAGE);
        while (menuHeroes.Count < pages * PER_PAGE)
            menuHeroes.Add(new Hero { Id = "", Name = "???", Role = Tr("СКОРО", "SOON"), About = Tr("Новый напарник придёт с обновлением мода", "A new partner arrives with a mod update"), Locked = true, SetTried = true });
        // Открываемся на странице активного героя.
        menuPage = 0;
        for (int i = 0; i < menuReal; i++) if (IsActive(menuHeroes[i])) menuPage = i / PER_PAGE;
        debugHover = hover;
        LayoutMenu();
        menuOpen = true;
        menuAt = DateTime.Now;
        pageAt = menuAt;
        menuTouch = menuAt;
        menuClosedAt = DateTime.MinValue;
        wanderTarget = double.NaN;
    }

    // Кадры героя для карточки и выхода: стойка (бюст) срочно, выход и текущая цепочка — следом.
    void Prepare(Hero h)
    {
        if (h.SetTried) return;
        h.SetTried = true;
        var set = OpenSet(h.Folder);
        if (set == null || !set.Names.Contains("idle_01")) return;
        h.Set = set;
        Want(set, "idle_01", true);
        WantSteps(set, enter);
        WantSteps(set, timeline);
        WantSteps(set, loop);
    }

    void CloseMenu()
    {
        if (!menuOpen) return;
        menuOpen = false;
        menuClosedAt = DateTime.Now;
        Cursor = Cursors.Default;
    }

    // Меню ушло с экрана: окно снова обычного размера (бюсты остаются в кадрах героев).
    void DropMenu()
    {
        menuHeroes = new List<Hero>();
        menuSettings = false;
        gearRect = prevRect = nextRect = updRect = RectangleF.Empty;
        setHits.Clear();
        cardRects = new RectangleF[0];
        hoverK = new float[0];
        cw = W;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { menuOpen = false; menuClosedAt = DateTime.MinValue; DropMenu(); }
    }

    // Панель над героем: по центру на нём, но в пределах окна DeepSeek Harness и экрана; окно — место героя плюс панель.
    void LayoutMenu()
    {
        int n = Math.Max(1, Math.Min(PER_PAGE, menuHeroes.Count - menuPage * PER_PAGE)), hx = (int)Math.Round(x);
        var scr = Screen.FromPoint(new Point(hx, groundY)).WorkingArea;
        int lo = Math.Max(scr.Left, boxL), hi = Math.Min(scr.Right, boxR);
        if (hi - lo < 420) { lo = scr.Left; hi = scr.Right; }
        int maxPanel = Math.Min(CW_MAX - 40, hi - lo - 16);
        int cardW = CARD_W;
        int pw = 2 * PAD + n * cardW + (n - 1) * GAP;
        if (pw > maxPanel)
        {
            cardW = Math.Max(120, (maxPanel - 2 * PAD - (n - 1) * GAP) / n);
            pw = 2 * PAD + n * cardW + (n - 1) * GAP;
        }
        pw = menuSettings ? SET_W : Math.Max(pw, 440);
        int ph = HEAD + (menuSettings ? SET_ROW * SET_ROWS + 6 : CARD_H) + FOOT;
        int pl = Math.Max(lo + 8, Math.Min(hx - pw / 2, hi - 8 - pw));
        pl = Math.Max(pl, hx + W / 2 + 16 - CW_MAX);
        pl = Math.Min(pl, hx - W / 2 + CW_MAX - 16 - pw);
        int left = Math.Min(hx - W / 2, pl - 16), right = Math.Max(hx + W / 2, pl + pw + 16);
        menuLeft = left;
        cw = Math.Min(CW_MAX, right - left);
        int py = GROUND + 6 - ph;
        // Окно DeepSeek Harness у самого верха экрана — панель не уходит за край.
        int top = groundY - GROUND;
        if (top + py < scr.Top + 8) py = Math.Min(H - ph - 8, scr.Top + 8 - top);
        panel = new Rectangle(pl - left, py, pw, ph);
        if (menuSettings) { cardRects = new RectangleF[0]; hoverK = new float[0]; return; }
        int total = n * cardW + (n - 1) * GAP;
        float sx0 = panel.X + (pw - total) / 2f;
        cardRects = new RectangleF[n];
        for (int i = 0; i < n; i++) cardRects[i] = new RectangleF(sx0 + i * (cardW + GAP), py + HEAD, cardW, CARD_H);
        hoverK = new float[n];
    }

    // Состояние меню за кадр: автозакрытие, клик вне окна, подсветка под мышью. Возвращает, на экране ли меню.
    bool UpdateMenu(double dt)
    {
        if (menuOpen)
        {
            double age = (DateTime.Now - menuAt).TotalSeconds;
            if ((DateTime.Now - menuTouch).TotalSeconds > MENU_LIFE || (age > 0.3 && Control.MouseButtons != MouseButtons.None && !winRect.Contains(Control.MousePosition)))
                CloseMenu();
        }
        bool isShown = MenuShown;
        if (!isShown)
        {
            if (menuHeroes.Count > 0) DropMenu();
            return false;
        }
        int hover = -1;
        if (menuOpen)
        {
            hover = HitCard(mouse);
            if (hover < 0 && mouse.X < 0) hover = debugHover;
        }
        for (int i = 0; i < hoverK.Length; i++)
            hoverK[i] += (float)(((i == hover ? 1 : 0) - hoverK[i]) * Math.Min(1, dt * 16));
        return true;
    }

    // Номер карточки на странице под точкой; закрытые слоты не выбираются.
    int HitCard(Point p)
    {
        for (int i = 0; i < cardRects.Length; i++)
            if (cardRects[i].Contains(p)) { var h = PageHero(i); return h != null && !h.Locked ? i : -1; }
        return -1;
    }

    Hero PageHero(int i)
    {
        int k = menuPage * PER_PAGE + i;
        return k >= 0 && k < menuHeroes.Count ? menuHeroes[k] : null;
    }

    void FlipPage(int d)
    {
        int pages = Math.Max(1, (menuHeroes.Count + PER_PAGE - 1) / PER_PAGE);
        menuPage = ((menuPage + d) % pages + pages) % pages;
        LayoutMenu();
        pageAt = DateTime.Now;
        menuTouch = pageAt;
    }

    // Обновление для кнопки в шапке меню: 0 — нет, 1 — вышло (кнопка), 2 — ставится, 3 — готово, нужен перезапуск.
    int UpdState()
    {
        if (updating) return 2;
        if (updatedOk || downloaded) return 3;
        return handledVer != "" && Newer(handledVer, localVer) ? 1 : 0;
    }

    // Страница настроек открывается той же анимацией, что и меню.
    void ShowSettings(bool on)
    {
        menuSettings = on;
        setHits.Clear();
        gearRect = RectangleF.Empty;
        LayoutMenu();
        menuAt = DateTime.Now;
        menuTouch = menuAt;
    }

    bool OverControl(Point p)
    {
        if (!menuSettings) return gearRect.Contains(p) || prevRect.Contains(p) || nextRect.Contains(p) || (updRect.Contains(p) && UpdState() == 1);
        foreach (var hit in setHits) if (hit.Key.Contains(p)) return true;
        return false;
    }

    void OnUp(MouseEventArgs e)
    {
        // Завершение драга: клик-реакция после сдвига НЕ срабатывает; позиция сохраняется.
        if (dragging)
        {
            dragging = false;
            Capture = false;
            bool moved = dragMoved;
            dragMoved = false;
            if (moved)
            {
                SaveSettings();
                Report("drag: home " + homeLeft + "," + homeTop);
                return;
            }
            // Сдвига не было — это обычный клик, обрабатываем ниже.
        }
        if (menuOpen)
        {
            menuTouch = DateTime.Now;
            if (menuSettings)
            {
                // ПКМ — назад к героям, клик мимо панели — закрыть.
                if (e.Button != MouseButtons.Left) { ShowSettings(false); return; }
                foreach (var hit in setHits) if (hit.Key.Contains(e.Location)) { hit.Value(); return; }
                if (!panel.Contains(e.Location)) CloseMenu();
                return;
            }
            if (e.Button == MouseButtons.Left && gearRect.Contains(e.Location)) { ShowSettings(true); return; }
            if (e.Button == MouseButtons.Left && prevRect.Contains(e.Location)) { FlipPage(-1); return; }
            if (e.Button == MouseButtons.Left && nextRect.Contains(e.Location)) { FlipPage(1); return; }
            if (e.Button == MouseButtons.Left && updRect.Contains(e.Location)) { if (UpdState() == 1) StartUpdate(); return; }
            int i = HitCard(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0)
            {
                if ((DateTime.Now - menuAt).TotalMilliseconds > 200) Pick(PageHero(i));
            }
            // ПКМ или клик мимо панели — закрыть; клик по самой панели между карточками ничего не делает.
            else if (e.Button != MouseButtons.Left || !panel.Contains(e.Location)) CloseMenu();
            return;
        }
        if (e.Button == MouseButtons.Right) OpenMenu();
        else if (e.Button == MouseButtons.Left) Clicked(e.Location);
    }

    bool IsActive(Hero h)
    {
        string key = string.IsNullOrEmpty(h.Sprite) ? h.Id : h.Sprite;
        return !string.IsNullOrEmpty(HeroKey) && (key == HeroKey || h.Id == HeroKey);
    }

    // Выбор героя по номеру в списке от мода (как клик по карточке; для проверок — assets/pet/pick.request).
    public void Choose(int index)
    {
        if (index >= 0 && index < roster.Count) Pick(roster[index]);
    }

    // Выбран напарник: сразу свои кадры, имя и выход, не дожидаясь мода (и что бы ни делал Claude); после выхода —
    // та же цепочка, что играла, уже новым героем. Мод узнает из choice.json.
    void Pick(Hero h)
    {
        CloseMenu();
        if (IsActive(h)) return;
        pendingLine = null;
        string key = string.IsNullOrEmpty(h.Sprite) ? h.Id : h.Sprite;
        WriteChoice(h.Id);
        SetHero(h.Folder);
        HeroKey = key;
        HeroName = h.Name ?? "";
        HeroRole = h.Role ?? "";
        heroOn = true;
        pickedKey = key;
        pickedAt = DateTime.Now;
        if (enter.Count > 0) StartOverlay(enter, true);
        Say((string.IsNullOrEmpty(HeroName) ? key : HeroName) + Tr(" на связи", " online"), true);
    }

    // choice.json для мода: кого выбрали и когда (unix, мс), UTF-8 без BOM.
    void WriteChoice(string id)
    {
        if (string.IsNullOrEmpty(ChoicePath) || string.IsNullOrEmpty(id)) return;
        long at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var sb = new StringBuilder("{\"hero\":\"");
        foreach (char ch in id)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < ' ') sb.AppendFormat("\\u{0:x4}", (int)ch);
            else sb.Append(ch);
        }
        sb.Append("\",\"at\":").Append(at).Append('}');
        try { File.WriteAllText(ChoicePath, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception) { }
    }

    static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }

    static Color Mix(Color a, Color b, double k)
    {
        k = Math.Max(0, Math.Min(1, k));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * k), (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }

    // Меню: панель раскрывается из светящейся полосы (~250 мс), шапка проявляется глитчем, карточки — по очереди.
    // При закрытии та же анимация идёт назад.
    void DrawMenu(double now)
    {
        double life = (DateTime.Now - menuAt).TotalMilliseconds;
        double unfold = menuOpen ? life : OPEN_MS * Math.Max(0, 1 - (DateTime.Now - menuClosedAt).TotalMilliseconds / CLOSE_MS);
        // Невидимая подложка на всё окно: клик мимо панели тоже попадает в меню и закрывает его.
        using (var b = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(b, 0, 0, cw, H);
        double wK = Ease(unfold / 90), hK = Ease((unfold - 60) / (OPEN_MS - 60));
        if (wK < 0.01) return;
        var p = panel;
        float mx = p.X + p.Width / 2f, my = p.Y + p.Height / 2f;
        float hw = (float)(p.Width / 2.0 * wK), hh = (float)Math.Max(1.5, p.Height / 2.0 * hK);
        var state = g.Save();
        if (hK < 1) g.SetClip(new RectangleF(mx - hw - 24, my - hh, hw * 2 + 48, hh * 2));
        if (hK > 0) DrawPanel(p, life, now);
        g.Restore(state);
        if (hK < 1)
        {
            // Кромки раскрытия: яркие линии со свечением сверху и снизу полосы.
            foreach (float ey in new[] { my - hh, my + hh })
            {
                for (int i = 3; i >= 1; i--)
                    using (var pen = new Pen(Color.FromArgb(40, RED), i * 3f)) g.DrawLine(pen, mx - hw, ey, mx + hw, ey);
                using (var pen = new Pen(Color.FromArgb(255, 255, 214, 214), 1.5f)) g.DrawLine(pen, mx - hw, ey, mx + hw, ey);
            }
        }
    }

    void DrawPanel(Rectangle p, double life, double now)
    {
        float x0 = p.X, y0 = p.Y, w = p.Width, h = p.Height, c = 22;
        var pts = new[]
        {
            new PointF(x0 + c, y0), new PointF(x0 + w, y0), new PointF(x0 + w, y0 + h - c),
            new PointF(x0 + w - c, y0 + h), new PointF(x0, y0 + h), new PointF(x0, y0 + c),
        };
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(pts);
            for (int i = 4; i >= 1; i--)
                using (var pen = new Pen(Color.FromArgb(22, RED), i * 4f)) g.DrawPath(pen, path);
            using (var fill = new LinearGradientBrush(new PointF(0, y0 - 1), new PointF(0, y0 + h + 1), Color.FromArgb(255, 20, 9, 13), Color.FromArgb(255, 7, 9, 13)))
                g.FillPath(fill, path);
            // Сканлайны и медленная полоса развёртки — только внутри панели.
            var st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using (var b = new SolidBrush(Color.FromArgb(9, 255, 255, 255)))
                for (float y = y0 + 1; y < y0 + h; y += 3) g.FillRectangle(b, x0, y, w, 1);
            float sweep = y0 - 70 + (float)((now * 80) % (h + 140));
            using (var lg = new LinearGradientBrush(new PointF(0, sweep - 1), new PointF(0, sweep + 71), Color.Transparent, Color.Transparent))
            {
                lg.InterpolationColors = new ColorBlend(3)
                {
                    Colors = new[] { Color.FromArgb(0, RED), Color.FromArgb(18, RED), Color.FromArgb(0, RED) },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillRectangle(lg, x0, sweep, w, 70);
            }
            g.Restore(st);
            using (var pen = new Pen(RED, 2)) g.DrawPath(pen, path);
        }
        // Уголки-скобки по углам без среза — как прицел в интерфейсе.
        using (var pen = new Pen(YELLOW, 2))
        {
            g.DrawLines(pen, new[] { new PointF(x0 + w - 16, y0 + 5), new PointF(x0 + w - 5, y0 + 5), new PointF(x0 + w - 5, y0 + 16) });
            g.DrawLines(pen, new[] { new PointF(x0 + 5, y0 + h - 16), new PointF(x0 + 5, y0 + h - 5), new PointF(x0 + 16, y0 + h - 5) });
        }

        DrawMenuHead(x0, y0, w, life);
        if (menuSettings) DrawSettings(x0, y0, w, life);
        // После листания карточки новой страницы проявляются по очереди, как при открытии.
        double cardLife = Math.Min(life, (DateTime.Now - pageAt).TotalMilliseconds + 150);
        for (int i = 0; i < cardRects.Length; i++)
        {
            var one = PageHero(i);
            if (one != null) DrawHeroCard(one, cardRects[i], hoverK[i], cardLife - 150 - i * 70, now, !one.Locked && IsActive(one), menuPage * PER_PAGE + i);
        }
        if (life > 220) DrawMenuFoot(x0, y0 + h - FOOT, w, life);
    }

    // Шапка «// ВЫБОР НАПАРНИКА»: буквы проявляются из случайных знаков, первые полсекунды — с цветным сдвигом.
    void DrawMenuHead(float x0, float y0, float w, double life)
    {
        float hx = x0 + PAD + 4, hy = y0 + 18;
        using (var b = new SolidBrush(RED)) g.FillRectangle(b, hx, hy + 2, 5, 22);
        string title = Scramble(menuSettings ? Tr("// НАСТРОЙКИ", "// SETTINGS") : Tr("// ВЫБОР НАПАРНИКА", "// CHOOSE PARTNER"), life);
        float tx = hx + 14;
        if (life < 420)
        {
            float jx = rnd.Next(-2, 3);
            using (var b = new SolidBrush(Color.FromArgb(170, RED))) g.DrawString(title, fMenuHead, b, tx - 2 + jx, hy, tight);
            using (var b = new SolidBrush(Color.FromArgb(170, CYAN))) g.DrawString(title, fMenuHead, b, tx + 2 + jx, hy, tight);
        }
        using (var b = new SolidBrush(YELLOW)) g.DrawString(title, fMenuHead, b, tx, hy, tight);

        updRect = RectangleF.Empty;
        int us = menuSettings ? 0 : UpdState();
        if (life > 120 && us > 0) DrawUpdateButton(x0 + w - PAD, hy + 1, us);
        else if (life > 120)
        {
            string sub = menuSettings ? Tr("СОХРАНЯЕТСЯ САМО", "SAVED AUTOMATICALLY") + (localVer != "" ? "  //  V" + localVer : "") :Tr("КАНАЛ ЗАЩИЩЁН  //  ", "SECURE CHANNEL  //  ") + menuReal.ToString("00");
            float sw = g.MeasureString(sub, fMenuSub, PointF.Empty, tight).Width;
            using (var b = new SolidBrush(CYAN)) g.DrawString(sub, fMenuSub, b, x0 + w - PAD - sw, hy + 8, tight);
        }
        // Разделитель: тонкая красная линия, жёлтый акцент под заголовком и риски у правого края.
        float ly = y0 + HEAD - 10;
        using (var pen = new Pen(Color.FromArgb(110, RED), 1)) g.DrawLine(pen, hx, ly, x0 + w - PAD, ly);
        using (var b = new SolidBrush(YELLOW)) g.FillRectangle(b, hx, ly - 1, 64, 3);
        using (var pen = new Pen(Color.FromArgb(170, RED), 1))
            for (int i = 0; i < 10; i++) g.DrawLine(pen, x0 + w - PAD - i * 7, ly - 5, x0 + w - PAD - i * 7, ly - 1);
    }

    string Scramble(string s, double life)
    {
        int done = (int)((life - 60) / 20);
        if (done >= s.Length) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length && i < done + 7; i++)
            sb.Append(i < done || s[i] == ' ' ? s[i] : GLYPHS[rnd.Next(GLYPHS.Length)]);
        return sb.ToString();
    }

    // Низ: подсказки клавиш и обратный отсчёт до автозакрытия (сегменты гаснут, в конце — красные).
    void DrawMenuFoot(float x0, float y0, float w, double life)
    {
        float fy = y0 + 16, fx = x0 + PAD + 4;
        fx = DrawHint(fx, fy, Tr("[ЛКМ]", "[LMB]"), YELLOW, menuSettings ? Tr("ИЗМЕНИТЬ", "CHANGE") : Tr("ВЫБРАТЬ", "SELECT"));
        fx = DrawHint(fx + 22, fy, Tr("[ПКМ]", "[RMB]"), RED, menuSettings ? Tr("НАЗАД", "BACK") : Tr("ЗАКРЫТЬ", "CLOSE"));
        if (!menuSettings) DrawGearButton(fx + 22, y0 + 9);
        prevRect = nextRect = RectangleF.Empty;
        // Листалка страниц — слева от отсчёта (в узкой панели — у правого края).
        if (!menuSettings && menuHeroes.Count > PER_PAGE) DrawPager(w < 460 ? x0 + w - PAD : x0 + w - PAD - 130, y0 + 9);
        // В узкой панели места под отсчёт нет.
        if (w < 460) return;
        double rest = Math.Max(0, 1 - (DateTime.Now - menuTouch).TotalSeconds / MENU_LIFE);
        int lit = (int)Math.Ceiling(rest * 12);
        Color on = rest < 0.25 ? RED : CYAN;
        using (var bOn = new SolidBrush(on))
        using (var bOff = new SolidBrush(Color.FromArgb(70, 60, 70, 80)))
            for (int i = 0; i < 12; i++)
                g.FillRectangle(i < lit ? bOn : bOff, x0 + w - PAD - 30 - (11 - i) * 7, fy + 3, 4, 10);
    }

    // Кнопка «настройки» внизу меню героев.
    void DrawGearButton(float x0, float y0)
    {
        string label = Tr("НАСТРОЙКИ", "SETTINGS");
        float tw = g.MeasureString(label, fKey, PointF.Empty, tight).Width;
        var r = new RectangleF(x0, y0, tw + 40, 26);
        gearRect = r;
        bool hot = r.Contains(mouse);
        CutBox(r, Color.FromArgb(255, hot ? 46 : 22, hot ? 16 : 18, hot ? 22 : 24), hot ? YELLOW : Color.FromArgb(170, RED));
        Gear(r.X + 15, r.Y + 13, 7, 0, hot ? YELLOW : CYAN, 1);
        using (var b = new SolidBrush(hot ? YELLOW : Color.FromArgb(226, 232, 240))) g.DrawString(label, fKey, b, r.X + 28, r.Y + 7, tight);
    }

    // «◀ СТР 1/3 ▶»: правый край — right, стрелки — кнопки в стиле панели.
    void DrawPager(float right, float y0)
    {
        int pages = (menuHeroes.Count + PER_PAGE - 1) / PER_PAGE;
        string label = Tr("СТР  ", "PAGE  ") + (menuPage + 1) + " / " + pages;
        float tw = g.MeasureString(label, fKey, PointF.Empty, tight).Width;
        nextRect = new RectangleF(right - 30, y0, 30, 26);
        prevRect = new RectangleF(nextRect.X - 16 - tw - 30, y0, 30, 26);
        using (var b = new SolidBrush(Color.FromArgb(226, 232, 240))) g.DrawString(label, fKey, b, prevRect.Right + 8, y0 + 7, tight);
        foreach (var r in new[] { prevRect, nextRect })
        {
            bool hot = r.Contains(mouse), left = r == prevRect;
            CutBox(r, Color.FromArgb(255, hot ? 46 : 22, hot ? 16 : 18, hot ? 22 : 24), hot ? YELLOW : Color.FromArgb(170, RED));
            float cx = r.X + r.Width / 2, cy = r.Y + 13, s = left ? 1 : -1;
            using (var b = new SolidBrush(hot ? YELLOW : CYAN))
                g.FillPolygon(b, new[] { new PointF(cx - 4 * s, cy), new PointF(cx + 3 * s, cy - 6), new PointF(cx + 3 * s, cy + 6) });
        }
    }

    // Кнопка обновления в шапке меню (правый край — right): вышло — пульсирует жёлтым, ставится, готово.
    void DrawUpdateButton(float right, float y0, int state)
    {
        string label = state == 1 ? Tr("ВЫШЛО ОБНОВЛЕНИЕ V" + handledVer + "  //  ОБНОВИТЬ", "UPDATE V" + handledVer + " IS OUT  //  INSTALL")
            : state == 2 ? Tr("ОБНОВЛЯЮСЬ…", "UPDATING…")
            : Tr("ОБНОВЛЕНО  //  ПЕРЕЗАПУСТИ DEEPSEEK HARNESS", "UPDATED  //  RESTART DEEPSEEK HARNESS");
        float tw = g.MeasureString(label, fKey, PointF.Empty, tight).Width;
        var r = new RectangleF(right - tw - 34, y0, tw + 34, 26);
        updRect = r;
        bool hot = state == 1 && r.Contains(mouse);
        double pulse = 0.5 + 0.5 * Math.Sin(Environment.TickCount / 1000.0 * 5);
        Color edge = state == 1 ? YELLOW : state == 2 ? Color.FromArgb(170, RED) : CYAN;
        if (state == 1)
            for (int i = 3; i >= 1; i--)
                using (var pen = new Pen(Color.FromArgb((int)(18 + 30 * pulse), YELLOW), i * 3f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        CutBox(r, state == 1 ? (hot ? YELLOW : Mix(Color.FromArgb(255, 60, 52, 8), Color.FromArgb(255, 120, 108, 10), pulse)) : Color.FromArgb(255, 22, 18, 24), edge);
        // Значок: стрелка вниз (скачать), у «готово» — галочка.
        float ix = r.X + 14, iy = r.Y + 13;
        Color ic = hot ? INK : edge;
        using (var pen = new Pen(ic, 2))
            if (state == 3) g.DrawLines(pen, new[] { new PointF(ix - 5, iy), new PointF(ix - 1, iy + 4), new PointF(ix + 6, iy - 4) });
            else { g.DrawLine(pen, ix, iy - 6, ix, iy + 3); g.DrawLines(pen, new[] { new PointF(ix - 4, iy - 1), new PointF(ix, iy + 4), new PointF(ix + 4, iy - 1) }); }
        using (var b = new SolidBrush(hot ? INK : state == 1 ? Color.White : Color.FromArgb(226, 232, 240))) g.DrawString(label, fKey, b, r.X + 26, r.Y + 7, tight);
    }

    // Прямоугольник со срезанным углом — кнопка в стиле панели.
    void CutBox(RectangleF r, Color fill, Color edge)
    {
        const float c = 7;
        var pts = new[] { new PointF(r.X, r.Y), new PointF(r.Right - c, r.Y), new PointF(r.Right, r.Y + c), new PointF(r.Right, r.Bottom), new PointF(r.X + c, r.Bottom), new PointF(r.X, r.Bottom - c) };
        using (var b = new SolidBrush(fill)) g.FillPolygon(b, pts);
        using (var pen = new Pen(edge, 1)) g.DrawPolygon(pen, pts);
    }

    // Страница настроек: подпись слева, значение справа — компактно, одна строка на настройку.
    void DrawSettings(float x0, float y0, float w, double life)
    {
        setHits.Clear();
        if (life < 140) return;
        float lx = x0 + PAD + 4, cx = x0 + 176, right = x0 + w - PAD - 2, y = y0 + HEAD + 2;
        SetRow(lx, y, right, Tr("ЯЗЫК", "LANGUAGE"), false);
        SetChoices(cx, y, right, new[] { "РУССКИЙ", "ENGLISH" }, lang == "en" ? 1 : 0, false, i => { lang = i == 1 ? "en" : "ru"; });
        y += SET_ROW;
        SetRow(lx, y, right, Tr("РАЗМЕР", "SIZE"), false);
        SetChoices(cx, y, right, new[] { "80%", "100%", "120%", "140%" }, Array.IndexOf(SIZES, sizePct), false, i => { sizePct = SIZES[i]; ApplySize(); });
        y += SET_ROW;
        SetRow(lx, y, right, Tr("ЗВОНОК", "CALL"), false);
        SetChoices(cx, y, right, new[] { Tr("ВКЛ", "ON"), Tr("ВЫКЛ", "OFF") }, callOn ? 0 : 1, false, i => { callOn = i == 0; });
        y += SET_ROW;
        SetRow(lx, y, right, Tr("ГРОМКОСТЬ", "VOLUME"), !callOn);
        SetVolume(cx, y, right, !callOn);
        y += SET_ROW;
        SetRow(lx, y, right, Tr("РЕПЛИКИ", "LINES"), false);
        SetChoices(cx, y, right, new[] { Tr("ЧАСТО", "OFTEN"), Tr("РЕДКО", "RARE"), Tr("ПО КЛИКУ", "ON CLICK") }, linesMode, false, i => { linesMode = i; });
        y += SET_ROW;
        SetRow(lx, y, right, Tr("ЭФФЕКТЫ", "EFFECTS"), false);
        SetChoices(cx, y, right, new[] { Tr("ВКЛ", "ON"), Tr("ВЫКЛ", "OFF") }, fxOn ? 0 : 1, false, i => { fxOn = i == 0; });
        y += SET_ROW;
        SetRow(lx, y, right, Tr("ПРОГУЛКИ", "WALKS"), false);
        SetChoices(cx, y, right, new[] { Tr("ВКЛ", "ON"), Tr("ВЫКЛ", "OFF") }, wanderOn ? 0 : 1, false, i => { wanderOn = i == 0; });
        y += SET_ROW;
        // Ручная позиция: кнопка возвращает питомца в правый нижний угол и забывает сохранённое место.
        SetRow(lx, y, right, Tr("ПОЛОЖЕНИЕ", "POSITION"), false);
        SetChoices(cx, y, right, new[] { Tr("СБРОСИТЬ", "RESET") }, -1, false, i => ResetHome());
        y += SET_ROW;
        // Мини-режим: вместо тела героя — маленькая аватарка-голова на том же месте.
        SetRow(lx, y, right, Tr("МИНИ-РЕЖИМ", "MINI MODE"), false);
        SetChoices(cx, y, right, new[] { Tr("ВКЛ", "ON"), Tr("ВЫКЛ", "OFF") }, miniOn ? 0 : 1, false, i => { miniOn = i == 0; SaveSettings(); Report("mini: " + miniOn); });
        y += SET_ROW;
        // Мониторинг: отдельное окно SYS.MON (второй layered/topmost Hwnd); toggle сразу показывает/скрывает его.
        SetRow(lx, y, right, Tr("МОНИТОРИНГ", "MONITOR"), false);
        SetChoices(cx, y, right, new[] { Tr("ВКЛ", "ON"), Tr("ВЫКЛ", "OFF") }, monitorOn ? 0 : 1, false, i =>
        {
            monitorOn = i == 0;
            SaveSettings();
            try { if (monitorOn) ShowMonitor(); else HideMonitor(); } catch (Exception e) { LastError = "mon: " + e.Message; }
            Report("monitor: " + monitorOn);
        });
        // Строки «ОБНОВЛЕНИЯ» и «GITHUB» из оригинала не показываем: в порте DSH обновления и звезда выключены.
    }

    void SetRow(float lx, float y, float right, string label, bool dim)
    {
        using (var b = new SolidBrush(dim ? Color.FromArgb(120, 130, 140, 150) : Color.FromArgb(232, 236, 242))) g.DrawString(label, fKey, b, lx, y + 10, tight);
        using (var pen = new Pen(Color.FromArgb(38, RED), 1)) g.DrawLine(pen, lx, y + SET_ROW - 3, right, y + SET_ROW - 3);
    }

    // Переключатель: выбранное — жёлтой плашкой, под мышью — яркая рамка. Любое изменение сразу в файл.
    void SetChoices(float x0, float y, float right, string[] items, int active, bool dim, Action<int> pick)
    {
        const float gap = 6;
        float bw = (right - x0 - gap * (items.Length - 1)) / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            var r = new RectangleF(x0 + i * (bw + gap), y + 4, bw, 26);
            bool on = i == active, hot = r.Contains(mouse);
            CutBox(r, on ? YELLOW : Color.FromArgb(255, 20, 16, 22), on ? YELLOW : hot ? RED : Color.FromArgb(90, RED));
            float tw = g.MeasureString(items[i], fKey, PointF.Empty, tight).Width;
            using (var b = new SolidBrush(on ? INK : hot ? Color.White : Color.FromArgb(190, 198, 208))) g.DrawString(items[i], fKey, b, r.X + (r.Width - tw) / 2, r.Y + 7, tight);
            int n = i;
            if (!dim) setHits.Add(new KeyValuePair<RectangleF, Action>(r, () => { pick(n); SaveSettings(); }));
        }
    }

    // Громкость: кнопка «0» и десять делений; клик ставит громкость и сразу даёт её послушать.
    void SetVolume(float x0, float y, float right, bool dim)
    {
        var mute = new RectangleF(x0, y + 4, 30, 26);
        bool hot = !dim && mute.Contains(mouse), off = volume == 0;
        CutBox(mute, off && !dim ? YELLOW : Color.FromArgb(255, 20, 16, 22), off && !dim ? YELLOW : hot ? RED : Color.FromArgb(90, RED));
        using (var b = new SolidBrush(off && !dim ? INK : Color.FromArgb(dim ? 110 : 200, 198, 208, 218))) g.DrawString("0", fKey, b, mute.X + 11, mute.Y + 7, tight);
        if (!dim) setHits.Add(new KeyValuePair<RectangleF, Action>(mute, () => SetLevel(0)));
        float sx = mute.Right + 8, gap = 3, sw = (right - sx - 9 * gap) / 10;
        for (int i = 0; i < 10; i++)
        {
            var r = new RectangleF(sx + i * (sw + gap), y + 6, sw, 22);
            bool lit = i < volume, over = !dim && r.Contains(mouse);
            Color c = lit ? Mix(CYAN, RED, i / 9.0) : Color.FromArgb(255, 30, 34, 42);
            if (dim) c = Color.FromArgb(lit ? 110 : 70, c);
            using (var b = new SolidBrush(c)) g.FillRectangle(b, r);
            if (over) using (var pen = new Pen(YELLOW, 1)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            int level = i + 1;
            if (!dim) setHits.Add(new KeyValuePair<RectangleF, Action>(new RectangleF(r.X - 1, y + 2, r.Width + gap, 32), () => SetLevel(level)));
        }
    }

    void SetLevel(int level)
    {
        volume = level;
        SaveSettings();
        PlayTest();
    }

    float DrawHint(float x0, float y0, string key, Color color, string label)
    {
        using (var b = new SolidBrush(color)) g.DrawString(key, fKey, b, x0, y0, tight);
        x0 += g.MeasureString(key, fKey, PointF.Empty, tight).Width + 7;
        using (var b = new SolidBrush(Color.FromArgb(206, 212, 220))) g.DrawString(label, fKey, b, x0, y0, tight);
        return x0 + g.MeasureString(label, fKey, PointF.Empty, tight).Width;
    }

    // Карточка героя: бюст как фото в досье, имя крупно жёлтым, роль бирюзовым, пара строк о нём, три полосы
    // характеристик сегментами. Под мышью — приподнята, ярче рамка и жёлтая кромка. Появляется с мерцанием.
    void DrawHeroCard(Hero h, RectangleF r, float hk, double appear, double now, bool active, int index)
    {
        if (appear < 0) return;
        if (appear < 200 && ((int)(appear / 40)) % 2 == 1) return;
        float jx = appear < 200 ? rnd.Next(-5, 6) : 0;
        float x0 = r.X + jx, y0 = r.Y - 3 * hk, w = r.Width, hgt = r.Height, c = 16;
        var pts = new[]
        {
            new PointF(x0, y0), new PointF(x0 + w - c, y0), new PointF(x0 + w, y0 + c),
            new PointF(x0 + w, y0 + hgt), new PointF(x0 + c, y0 + hgt), new PointF(x0, y0 + hgt - c),
        };
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(pts);
            if (hk > 0.02)
                for (int i = 3; i >= 1; i--)
                    using (var pen = new Pen(Color.FromArgb((int)(36 * hk), RED), i * 4f)) g.DrawPath(pen, path);
            using (var b = new SolidBrush(Color.FromArgb(242, (int)(17 + 16 * hk), (int)(11 + 4 * hk), (int)(15 + 4 * hk)))) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb((int)(120 + 135 * hk), RED), 1.5f)) g.DrawPath(pen, path);
        }
        if (hk > 0.02)
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * hk), YELLOW))) g.FillRectangle(b, x0 - 1, y0 + 22, 3, hgt - 50);

        // Портрет: сетка, бюст (или «НЕТ СИГНАЛА»), сканлайны, затемнение книзу, уголки-скобки.
        var pr = new RectangleF(x0 + 10, y0 + 10, w - 20, PORT_H);
        using (var b = new SolidBrush(Color.FromArgb(255, 7, 14, 18))) g.FillRectangle(b, pr);
        using (var pen = new Pen(Color.FromArgb(20, CYAN), 1))
        {
            for (float gx = pr.X + 14; gx < pr.Right; gx += 14) g.DrawLine(pen, gx, pr.Y, gx, pr.Bottom);
            for (float gy = pr.Y + 14; gy < pr.Bottom; gy += 14) g.DrawLine(pen, pr.X, gy, pr.Right, gy);
        }
        Prepare(h);
        // Бюст готовит фоновый поток; пока он не готов — пустая сетка, а без кадров — «НЕТ СИГНАЛА».
        var bust = h.Set != null ? h.Set.Bust : null;
        if (bust != null) g.DrawImage(bust, pr.X, pr.Y, pr.Width, pr.Height);
        else if (h.Set == null) NoSignal(pr, now, h.Locked);
        using (var b = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            for (float sy0 = pr.Y + 1; sy0 < pr.Bottom; sy0 += 3) g.FillRectangle(b, pr.X, sy0, pr.Width, 1);
        using (var lg = new LinearGradientBrush(new PointF(0, pr.Bottom - 37), new PointF(0, pr.Bottom + 1), Color.FromArgb(0, 18, 11, 15), Color.FromArgb(215, 18, 11, 15)))
        {
            lg.WrapMode = WrapMode.TileFlipXY;
            g.FillRectangle(lg, pr.X, pr.Bottom - 36, pr.Width, 36);
        }
        using (var pen = new Pen(Color.FromArgb((int)(150 + 105 * hk), CYAN), 1.5f))
        {
            float L = 12;
            g.DrawLines(pen, new[] { new PointF(pr.X, pr.Y + L), new PointF(pr.X, pr.Y), new PointF(pr.X + L, pr.Y) });
            g.DrawLines(pen, new[] { new PointF(pr.Right - L, pr.Y), new PointF(pr.Right, pr.Y), new PointF(pr.Right, pr.Y + L) });
            g.DrawLines(pen, new[] { new PointF(pr.X, pr.Bottom - L), new PointF(pr.X, pr.Bottom), new PointF(pr.X + L, pr.Bottom) });
            g.DrawLines(pen, new[] { new PointF(pr.Right - L, pr.Bottom), new PointF(pr.Right, pr.Bottom), new PointF(pr.Right, pr.Bottom - L) });
        }
        string num = (index + 1).ToString("00");
        float nw = g.MeasureString(num, fTag, PointF.Empty, tight).Width;
        using (var b = new SolidBrush(Color.FromArgb(200, YELLOW))) g.DrawString(num, fTag, b, pr.Right - 7 - nw, pr.Y + 7, tight);
        if (active)
        {
            // Метка «АКТИВЕН»: бирюзовая плашка с мигающей точкой.
            string ON = Tr("АКТИВЕН", "ACTIVE");
            float tw = g.MeasureString(ON, fTag, PointF.Empty, tight).Width;
            var tag = new RectangleF(pr.X + 7, pr.Y + 7, tw + 24, 16);
            using (var b = new SolidBrush(CYAN)) g.FillRectangle(b, tag);
            if (((int)(now / 0.5)) % 2 == 0)
                using (var b = new SolidBrush(INK)) g.FillEllipse(b, tag.X + 6, tag.Y + 5, 6, 6);
            using (var b = new SolidBrush(INK)) g.DrawString(ON, fTag, b, tag.X + 16, tag.Y + 3, tight);
        }

        // Имя, роль, пара строк о герое.
        float ty = pr.Bottom + 9;
        string name = (string.IsNullOrEmpty(h.Name) ? h.Id ?? "" : h.Name).ToUpperInvariant();
        var nf = g.MeasureString(name, fName, PointF.Empty, tight).Width > w - 20 ? fNameSmall : fName;
        var nameBox = new RectangleF(x0 + 10, ty, w - 20, 34);
        if (hk > 0.02)
            using (var b = new SolidBrush(Color.FromArgb((int)(150 * hk), RED)))
                g.DrawString(name, nf, b, new RectangleF(nameBox.X + 2, nameBox.Y + 1, nameBox.Width, nameBox.Height), tightLine);
        using (var b = new SolidBrush(YELLOW)) g.DrawString(name, nf, b, nameBox, tightLine);
        ty += 34;
        if (!string.IsNullOrEmpty(h.Role))
        {
            using (var b = new SolidBrush(CYAN))
            {
                g.FillRectangle(b, x0 + 10, ty + 4, 5, 5);
                g.DrawString(h.Role.ToUpperInvariant(), fRole, b, new RectangleF(x0 + 20, ty, w - 30, 16), tightLine);
            }
        }
        ty += 20;
        if (!string.IsNullOrEmpty(h.About))
            using (var b = new SolidBrush(Color.FromArgb(178, 186, 196)))
                g.DrawString(h.About, fAbout, b, new RectangleF(x0 + 10, ty, w - 20, 44), aboutFormat);

        // Характеристики: подпись слева, справа десять скошенных сегментов; под мышью заливка желтеет.
        int rows = Math.Min(3, Math.Min(h.Labels.Length, h.Values.Length));
        float rowH = 17, sy0r = y0 + hgt - 18 - rows * rowH;
        if (rows == 0)
        {
            using (var b = new SolidBrush(Color.FromArgb(150, RED))) g.DrawString(Tr("ДОСЬЕ ЗАСЕКРЕЧЕНО", "FILE CLASSIFIED"), fStat, b, x0 + 10, y0 + hgt - 34, tight);
            return;
        }
        float barX = x0 + 10 + 78, barW = w - 20 - 78, segGap = 2.5f, segW = (barW - 9 * segGap) / 10;
        Color fillOn = Mix(RED, YELLOW, hk);
        using (var lab = new SolidBrush(MUTED))
        using (var on = new SolidBrush(fillOn))
        using (var off = new SolidBrush(Color.FromArgb(255, 58, 24, 30)))
            for (int j = 0; j < rows; j++)
            {
                float ry = sy0r + j * rowH;
                g.DrawString(h.Labels[j].ToUpperInvariant(), fStat, lab, new RectangleF(x0 + 10, ry, 76, rowH), tightLine);
                int litSeg = (int)Math.Round(Math.Max(0, Math.Min(1, h.Values[j])) * 10);
                for (int s = 0; s < 10; s++)
                {
                    float px = barX + s * (segW + segGap), py = ry + 3;
                    g.FillPolygon(s < litSeg ? on : off, new[]
                    {
                        new PointF(px + 2.5f, py), new PointF(px + segW + 2.5f, py), new PointF(px + segW, py + 8), new PointF(px, py + 8),
                    });
                }
            }
    }

    // Нет кадров героя: тёмный силуэт в помехах и мигающее «НЕТ СИГНАЛА».
    void NoSignal(RectangleF r, double now, bool locked = false)
    {
        var st = g.Save();
        g.SetClip(r, CombineMode.Intersect);
        float cx = r.X + r.Width / 2;
        using (var b = new SolidBrush(Color.FromArgb(255, 24, 29, 35)))
        {
            g.FillEllipse(b, cx - 25, r.Y + 24, 50, 60);
            g.FillEllipse(b, cx - 68, r.Y + 92, 136, 130);
        }
        using (var b = new SolidBrush(Color.Gray))
            for (int i = 0; i < 110; i++)
            {
                int v = rnd.Next(60, 200);
                b.Color = Color.FromArgb(rnd.Next(25, 90), v, v, v);
                g.FillRectangle(b, r.X + rnd.Next((int)r.Width), r.Y + rnd.Next((int)r.Height), rnd.Next(1, 5), 1);
            }
        using (var b = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
            g.FillRectangle(b, r.X, r.Y + (float)((now * 70) % r.Height), r.Width, 3);
        g.Restore(st);
        string NO = locked ? Tr("СЛОТ ЗАКРЫТ", "LOCKED") : Tr("НЕТ СИГНАЛА", "NO SIGNAL");
        var size = g.MeasureString(NO, fRole, PointF.Empty, tight);
        var plate = new RectangleF(cx - size.Width / 2 - 8, r.Y + r.Height / 2 - 11, size.Width + 16, 22);
        using (var b = new SolidBrush(Color.FromArgb(220, 10, 8, 10))) g.FillRectangle(b, plate);
        using (var pen = new Pen(Color.FromArgb(160, RED), 1)) g.DrawRectangle(pen, plate.X, plate.Y, plate.Width, plate.Height);
        if (locked || ((int)(now / 0.55)) % 2 == 0)
            using (var b = new SolidBrush(RED)) g.DrawString(NO, fRole, b, plate.X + 8, plate.Y + 5, tight);
    }

    void Present(int left, int top, int width, int height)
    {
        g.Flush(FlushIntention.Sync);
        IntPtr screen = GetDC(IntPtr.Zero);
        var dst = new POINT(left, top);
        var sz = new SIZE(width, height);
        var src = new POINT(0, 0);
        var blend = new BLEND { op = 0, flags = 0, alpha = 255, format = 1 };
        UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDc, ref src, 0, ref blend, 2);
        ReleaseDC(IntPtr.Zero, screen);
        lastWidth = width;
        lastHeight = height;
        winRect = new Rectangle(left, top, width, height);
        if (recFrames != null && height == H) RecordFrame(left, width);
    }

    // ---- запись для роликов: assets/pet/record.request с текстом «папка|секунды» ----
    // Кадры копируются в память, чтобы не мешать анимации, и сохраняются в PNG с прозрачностью после конца записи.
    // Герой всегда в одной точке кадра, напарник и эффекты слева от него помещаются. Карточка реплики не пишется.
    const int REC_W = 1200, REC_FPS = 30, REC_HERO_X = 900;
    string recordRequest, recDir;
    List<Bitmap> recFrames;
    DateTime recStart;
    double recSeconds;
    int recHx, recOriginHx, recCount;
    bool recWander;

    void StartRecord(string dir, double seconds)
    {
        if (recFrames != null) return;
        Directory.CreateDirectory(dir);
        recDir = dir;
        recSeconds = seconds;
        recOriginHx = recHx;
        recCount = 0;
        recWander = wanderOn;
        wanderOn = false;
        wanderTarget = double.NaN;
        recStart = DateTime.Now;
        recFrames = new List<Bitmap>((int)(seconds * REC_FPS) + 4);
    }

    void RecordFrame(int left, int width)
    {
        double t = (DateTime.Now - recStart).TotalSeconds;
        int want = (int)(t * REC_FPS);
        if (want >= recCount)
        {
            var bmp = new Bitmap(REC_W, H, PixelFormat.Format32bppPArgb);
            using (var cg = Graphics.FromImage(bmp))
            {
                cg.CompositingMode = CompositingMode.SourceCopy;
                cg.InterpolationMode = InterpolationMode.NearestNeighbor;
                cg.DrawImage(canvas, new Rectangle(left - recOriginHx + REC_HERO_X, 0, width, H), new Rectangle(0, 0, width, H), GraphicsUnit.Pixel);
            }
            // Если кадр анимации задержался, тот же снимок занимает и пропущенное место: время ролика не плывёт.
            for (; recCount <= want; recCount++) recFrames.Add(bmp);
        }
        if (t < recSeconds) return;
        var frames = recFrames;
        var dir = recDir;
        recFrames = null;
        wanderOn = recWander;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var seen = new HashSet<Bitmap>();
                for (int i = 0; i < frames.Count; i++)
                {
                    frames[i].Save(Path.Combine(dir, (i + 1).ToString("000000") + ".png"), ImageFormat.Png);
                    seen.Add(frames[i]);
                }
                foreach (var b in seen) b.Dispose();
                File.WriteAllText(Path.Combine(dir, "done.txt"), frames.Count.ToString());
            }
            catch (Exception e) { LastError = "record: " + e.Message; }
        });
    }

    // Снимок холста в PNG — проверить, как питомец выглядит, не видя экрана.
    public void Snapshot(string path)
    {
        g.Flush(FlushIntention.Sync);
        int w = Math.Max(1, Math.Min(CW_MAX, lastWidth));
        int h = Math.Max(1, Math.Min(H, lastHeight));
        // Папка preview рядом с assets создаётся при первом снимке (в чистой установке её нет).
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)); } catch (Exception) { }
        using (var copy = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var cg = Graphics.FromImage(copy))
            {
                cg.CompositingMode = CompositingMode.SourceCopy;
                cg.DrawImage(canvas, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
            }
            copy.Save(path, ImageFormat.Png);
        }
    }

    bool disposed;
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            closing = true;
            RemoveBeat();
            lock (jobLock) Monitor.PulseAll(jobLock);
            if (disposing)
            {
                if (testPlayer != null) { try { testPlayer.Dispose(); } catch (Exception) { } testPlayer = null; }
                if (miniAvatar != null) { try { miniAvatar.Dispose(); } catch (Exception) { } miniAvatar = null; }
                // Панель — отдельное окно: закрываем вместе с питомцем.
                if (monForm != null) { try { monForm.Close(); } catch (Exception) { } try { monForm.Dispose(); } catch (Exception) { } monForm = null; }
                frameTimer.Dispose();
                followTimer.Dispose();
                foreach (var s in new List<FrameSet>(sets.Values)) DropSet(s);
                DropMenu();
                g.Dispose();
                canvas.Dispose();
                foreach (var f in fonts) f.Dispose();
                tight.Dispose();
                tightLine.Dispose();
                aboutFormat.Dispose();
                fxB.Dispose();
                fxP.Dispose();
                fxDash.Dispose();
                coAttr.Dispose();
                if (cardChrome != null) cardChrome.Dispose();
                if (cardText != null) cardText.Dispose();
            }
            SelectObject(memDc, oldBmp);
            DeleteObject(dib);
            DeleteDC(memDc);
        }
        base.Dispose(disposing);
    }
}
