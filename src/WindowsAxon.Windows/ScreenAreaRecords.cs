namespace WindowsAxon.Windows;

internal readonly record struct ScreenPoint(double X, double Y);

internal readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => !(Width > 0 && Height > 0);

    public bool Contains(ScreenRect other) => other.X >= X
        && other.Y >= Y
        && other.X + other.Width <= X + Width
        && other.Y + other.Height <= Y + Height;
}
