using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Minimal JSON writer for the /ferl/* payloads. JsonUtility cannot emit nested arrays
// (footprint vertices are [[x, y], ...]) or null (carried_object_id), and the project
// has no Newtonsoft package, so the fixed scene-graph schema is written by hand.
public sealed class FerlJsonWriter
{
    private readonly StringBuilder builder = new StringBuilder(1024);
    // One flag per open container: whether a comma is needed before the next item.
    private readonly Stack<bool> needsComma = new Stack<bool>();

    public override string ToString() => builder.ToString();

    private void BeforeValue()
    {
        if (needsComma.Count == 0)
            return;
        if (needsComma.Pop())
            builder.Append(',');
        needsComma.Push(true);
    }

    public FerlJsonWriter BeginObject()
    {
        BeforeValue();
        builder.Append('{');
        needsComma.Push(false);
        return this;
    }

    public FerlJsonWriter EndObject()
    {
        needsComma.Pop();
        builder.Append('}');
        return this;
    }

    public FerlJsonWriter BeginArray()
    {
        BeforeValue();
        builder.Append('[');
        needsComma.Push(false);
        return this;
    }

    public FerlJsonWriter EndArray()
    {
        needsComma.Pop();
        builder.Append(']');
        return this;
    }

    public FerlJsonWriter Key(string name)
    {
        BeforeValue();
        AppendString(name);
        builder.Append(':');
        // The value that follows must not get a comma: mark the container as "fresh".
        needsComma.Pop();
        needsComma.Push(false);
        return this;
    }

    public FerlJsonWriter Value(string value)
    {
        BeforeValue();
        if (value == null)
            builder.Append("null");
        else
            AppendString(value);
        return this;
    }

    public FerlJsonWriter Value(double value)
    {
        BeforeValue();
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = 0.0;
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    public FerlJsonWriter Value(int value)
    {
        BeforeValue();
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public FerlJsonWriter Value(bool value)
    {
        BeforeValue();
        builder.Append(value ? "true" : "false");
        return this;
    }

    public FerlJsonWriter Null()
    {
        BeforeValue();
        builder.Append("null");
        return this;
    }

    public FerlJsonWriter Vector(double x, double y, double z)
    {
        BeginArray().Value(x).Value(y).Value(z).EndArray();
        return this;
    }

    public FerlJsonWriter Doubles(IList<double> values)
    {
        BeginArray();
        for (int i = 0; i < values.Count; i++)
            Value(values[i]);
        EndArray();
        return this;
    }

    public FerlJsonWriter Strings(IList<string> values)
    {
        BeginArray();
        for (int i = 0; i < values.Count; i++)
            Value(values[i]);
        EndArray();
        return this;
    }

    private void AppendString(string value)
    {
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
