namespace Galimapia.Canvas;

internal interface ISheetEdit
{
    void Undo(MapScene scene);
    void Redo(MapScene scene);
}

internal readonly record struct SheetPose(
    double X,
    double Y,
    double Rotation,
    double Scale,
    bool FlipX,
    bool FlipY,
    float Opacity,
    float Brightness,
    float Contrast,
    float Hue)
{
    public static SheetPose Capture(MapFragment fragment) =>
        new(
            fragment.X,
            fragment.Y,
            fragment.RotationDegrees,
            fragment.Scale,
            fragment.FlipX,
            fragment.FlipY,
            fragment.Opacity,
            fragment.Brightness,
            fragment.Contrast,
            fragment.Hue);

    public void Apply(MapFragment fragment)
    {
        fragment.X = X;
        fragment.Y = Y;
        fragment.RotationDegrees = Rotation;
        fragment.Scale = Scale;
        fragment.FlipX = FlipX;
        fragment.FlipY = FlipY;
        fragment.Opacity = Opacity;
        fragment.Brightness = Brightness;
        fragment.Contrast = Contrast;
        fragment.Hue = Hue;
    }
}

internal sealed class PoseEdit : ISheetEdit
{
    private readonly string _id;
    private readonly SheetPose _before;
    private readonly SheetPose _after;

    public PoseEdit(string id, SheetPose before, SheetPose after)
    {
        _id = id;
        _before = before;
        _after = after;
    }

    public void Undo(MapScene scene) => scene.ApplyPose(_id, _before);

    public void Redo(MapScene scene) => scene.ApplyPose(_id, _after);
}

internal sealed class DeleteEdit : ISheetEdit
{
    private readonly MapFragment _fragment;
    private readonly int _index;

    public DeleteEdit(MapFragment fragment, int index)
    {
        _fragment = fragment;
        _index = index;
    }

    public void Undo(MapScene scene) => scene.Insert(_index, _fragment);

    public void Redo(MapScene scene) => scene.Remove(_fragment);
}

internal sealed class OrderEdit : ISheetEdit
{
    private readonly int _from;
    private readonly int _to;

    public OrderEdit(int from, int to)
    {
        _from = from;
        _to = to;
    }

    public void Undo(MapScene scene) => scene.MoveItem(_to, _from);

    public void Redo(MapScene scene) => scene.MoveItem(_from, _to);
}

internal sealed class CropEdit : ISheetEdit
{
    private readonly string _id;
    private readonly CropPoint[] _before;
    private readonly CropPoint[] _after;

    private readonly bool _beforeRect;
    private readonly bool _afterRect;
    private readonly bool _beforeCommitted;
    private readonly bool _afterCommitted;
    private readonly double _beforeX;
    private readonly double _beforeY;
    private readonly double _afterX;
    private readonly double _afterY;
    private readonly CropPoint[] _beforeWarp;
    private readonly CropPoint[] _afterWarp;

    public CropEdit(
        string id,
        CropPoint[] before,
        bool beforeRect,
        bool beforeCommitted,
        double beforeX,
        double beforeY,
        CropPoint[] after,
        bool afterRect,
        bool afterCommitted,
        double afterX,
        double afterY,
        CropPoint[] beforeWarp,
        CropPoint[] afterWarp)
    {
        _id = id;
        _before = before;
        _beforeRect = beforeRect;
        _beforeCommitted = beforeCommitted;
        _beforeX = beforeX;
        _beforeY = beforeY;
        _after = after;
        _afterRect = afterRect;
        _afterCommitted = afterCommitted;
        _afterX = afterX;
        _afterY = afterY;
        _beforeWarp = beforeWarp;
        _afterWarp = afterWarp;
    }

    public void Undo(MapScene scene) =>
        scene.ReplaceCrop(_id, _before, _beforeRect, _beforeCommitted, _beforeX, _beforeY, _beforeWarp);

    public void Redo(MapScene scene) =>
        scene.ReplaceCrop(_id, _after, _afterRect, _afterCommitted, _afterX, _afterY, _afterWarp);
}

internal sealed class WarpEdit : ISheetEdit
{
    private readonly string _id;
    private readonly CropPoint[] _before;
    private readonly CropPoint[] _after;

    public WarpEdit(string id, CropPoint[] before, CropPoint[] after)
    {
        _id = id;
        _before = before;
        _after = after;
    }

    public void Undo(MapScene scene) => scene.ReplaceWarp(_id, _before);

    public void Redo(MapScene scene) => scene.ReplaceWarp(_id, _after);
}
