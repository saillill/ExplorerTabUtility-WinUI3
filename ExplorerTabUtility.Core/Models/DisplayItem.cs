namespace ExplorerTabUtility.Models;

public class DisplayItem<T>
{
    public string Display { get; }
    public T Value { get; }

    public DisplayItem(string display, T value)
    {
        Display = display;
        Value = value;
    }

    public override string ToString() => Display;
}
