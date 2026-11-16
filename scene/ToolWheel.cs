// The wheel: Wolfenstein's weapon wheel (The New Order, The New Colossus), after the Observatory's curio wheel
// (scripts/curio_wheel.gd there), empty for now. Hold Tab and the scene's time slows to a quarter, the wheel opens round
// the screen's centre and the pointer (or the mouse, while the view is locked) picks a slot; let go of Tab to hold what it
// points at. A click picks too. A tap of Tab leaves it open until a pick, Tab again or Escape, which shuts it holding
// what was held. Items is where things to hold will go; until then the slots are empty and nothing is held. Kept apart
// from the window, which draws it (Panel.Wheel), so that it can be tested alone.
internal sealed class ToolWheel
{
    public const float Slow = 0.25f; // the scene's time while the wheel is open
    public const float Tap = 0.3f; // s: Tab let go sooner leaves the wheel open
    public const int EmptySlots = 8; // drawn while there is nothing to hold
    private const float OpenS = 0.15f;

    private double tabAt;
    private float aimX, aimY; // the locked mouse's way from the centre, in pixels

    public IReadOnlyList<string> Items { get; init; } = [];

    public int Slots => Items.Count > 0 ? Items.Count : EmptySlots;

    public bool Open { get; private set; }

    public bool Sticky { get; private set; }

    public float Shown { get; private set; } // 0 shut to 1 open, eased

    public int Pick { get; private set; } = -1;

    public string? Held { get; private set; }

    public int Opens { get; private set; }

    public float TimeScale => Open ? Slow : 1f;

    // Tab pressed at the given second: opens the wheel, or shuts one left open.
    public void TabDown(double at)
    {
        if (Open && Sticky)
        {
            Close(true);
        }
        else if (!Open)
        {
            Show();
            tabAt = at;
        }
    }

    // Tab let go: a tap leaves the wheel open, a hold shuts it on what it points at.
    public void TabUp(double at)
    {
        if (!Open || Sticky)
        {
            return;
        }

        if (at - tabAt < Tap)
        {
            Sticky = true;
        }
        else
        {
            Close(true);
        }
    }

    // Shuts the wheel, holding what it points at, or (keep false: Escape) what was held.
    public void Close(bool pick)
    {
        if (!Open)
        {
            return;
        }

        Open = false;
        Sticky = false;
        if (pick && Pick >= 0 && Pick < Items.Count)
        {
            Held = Items[Pick];
        }
    }

    // The pointer, free, at x, y pixels from the screen's centre; inner is the wheel's hole, in pixels.
    public void Point(float x, float y, float inner)
    {
        if (Open && ((x * x) + (y * y)) > inner * 0.6f * inner * 0.6f)
        {
            Pick = Slot(x, y, Slots);
        }
    }

    // The locked mouse moved by dx, dy pixels: its way from the centre, at most outer, picks once past the hole.
    public void Aim(float dx, float dy, float inner, float outer)
    {
        if (!Open)
        {
            return;
        }

        (aimX, aimY) = (aimX + dx, aimY + dy);
        var length = MathF.Sqrt((aimX * aimX) + (aimY * aimY));
        if (length > outer)
        {
            (aimX, aimY) = (aimX / length * outer, aimY / length * outer);
        }

        if (length > inner * 0.6f)
        {
            Pick = Slot(aimX, aimY, Slots);
        }
    }

    // The opening's ease, by real time: the wheel grows from 85 % as it fades in.
    public void Advance(float realDt) => Shown = Math.Clamp(Shown + ((Open ? 1f : -1f) * realDt / OpenS), 0f, 1f);

    // The slot at x, y from the centre (y down), the first at the top, clockwise.
    public static int Slot(float x, float y, int slots)
    {
        var a = MathF.Atan2(y, x) + (MathF.PI * 0.5f);
        var i = (int)MathF.Round(a / (2f * MathF.PI / slots));
        return ((i % slots) + slots) % slots;
    }

    // The wheel's radii in pixels for a screen: larger on one held upright.
    public static (float Inner, float Outer) Radii(int width, int height)
    {
        var outer = (width < height ? 0.44f : 0.36f) * Math.Min(width, height);
        return (outer * 0.44f, outer);
    }

    private void Show()
    {
        Open = true;
        Sticky = false;
        (aimX, aimY) = (0f, 0f);
        Pick = Held is { } held ? Items.ToList().IndexOf(held) : -1;
        Opens++;
    }

    internal static void SelfTest(Action<bool, string> Check)
    {
        var wheel = new ToolWheel();
        Check(!wheel.Open && wheel.TimeScale == 1f && wheel.Slots == EmptySlots && wheel.Items.Count == 0, "the wheel starts shut, with its slots empty");

        wheel.TabDown(10.0);
        Check(wheel.Open && wheel.TimeScale == Slow, "Tab held opens it and slows the scene to a quarter");
        wheel.Point(0f, -200f, 50f);
        Check(wheel.Pick == 0, "the pointer straight up picks the first slot");
        wheel.Point(200f, 0f, 50f);
        Check(wheel.Pick == 2, "to the right, the third of eight, clockwise");
        wheel.Point(5f, 5f, 50f);
        Check(wheel.Pick == 2, "inside the hole the pick stays");
        wheel.TabUp(11.0);
        Check(!wheel.Open && wheel.TimeScale == 1f && wheel.Held is null, "Tab let go after a hold shuts it; an empty slot holds nothing");

        wheel.TabDown(20.0);
        wheel.TabUp(20.1);
        Check(wheel.Open && wheel.Sticky, "a tap leaves it open");
        wheel.TabDown(25.0);
        Check(!wheel.Open, "Tab again shuts it");

        var held = new ToolWheel { Items = ["a", "b", "c", "d"] };
        held.TabDown(0);
        held.Aim(0f, 30f, 50f, 150f);
        Check(held.Pick == -1, "the locked mouse picks only once past the hole");
        held.Aim(0f, 400f, 50f, 150f);
        Check(held.Pick == 2, "and then the slot it points to");
        held.Close(true);
        Check(held.Held == "c", "an item, when there are some, is held");
        held.TabDown(1);
        held.Aim(300f, -400f, 50f, 150f);
        held.Close(false);
        Check(held.Held == "c", "Escape shuts it holding what was held");

        var (inner, outer) = Radii(1280, 720);
        Check(MathF.Abs(outer - 259.2f) < 0.01f && MathF.Abs(inner - (outer * 0.44f)) < 0.01f, "its size is the Observatory's");

        wheel.Advance(1f);
        Check(wheel.Shown == 0f, "shut, it fades out");
    }
}
