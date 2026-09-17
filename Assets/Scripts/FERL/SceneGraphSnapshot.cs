using System.Collections.Generic;

// One object of the scene graph exactly as preference_rl's ObjectNode.to_dict() lays it out.
// Positions are in the robot base frame (ROS axes, z up); yaw is the heading about ROS z;
// footprint vertices are xy offsets from the position in the object's own (unturned) frame;
// zLow/zHigh are offsets from position.z.
public sealed class ObjectNodeData
{
    public string id;
    public string type;
    public double[] position = new double[3];
    public List<double[]> vertices = new List<double[]>();
    public double zLow;
    public double zHigh;
    public double[] attributes = new double[4];
    public bool editable;
    public double yaw;

    public void WriteJson(FerlJsonWriter writer)
    {
        writer.BeginObject();
        writer.Key("id").Value(id);
        writer.Key("type").Value(type);
        writer.Key("position").Vector(position[0], position[1], position[2]);
        writer.Key("footprint").BeginObject();
        writer.Key("vertices").BeginArray();
        foreach (double[] vertex in vertices)
            writer.BeginArray().Value(vertex[0]).Value(vertex[1]).EndArray();
        writer.EndArray();
        writer.Key("z_range").BeginArray().Value(zLow).Value(zHigh).EndArray();
        writer.EndObject();
        writer.Key("attributes").Doubles(attributes);
        writer.Key("editable").Value(editable);
        writer.Key("yaw").Value(yaw);
        writer.EndObject();
    }
}

// SceneGraph.to_dict(): the payload of /ferl/scene_graph and of every env-trace snapshot.
public sealed class SceneGraphSnapshot
{
    public List<ObjectNodeData> objects = new List<ObjectNodeData>();
    public double[] workspaceLow = new double[3];
    public double[] workspaceHigh = new double[3];
    public string carriedObjectId;

    public void WriteJson(FerlJsonWriter writer)
    {
        writer.BeginObject();
        writer.Key("objects").BeginArray();
        foreach (ObjectNodeData node in objects)
            node.WriteJson(writer);
        writer.EndArray();
        writer.Key("workspace").BeginArray();
        for (int axis = 0; axis < 3; axis++)
            writer.BeginArray().Value(workspaceLow[axis]).Value(workspaceHigh[axis]).EndArray();
        writer.EndArray();
        writer.Key("carried_object_id").Value(carriedObjectId);
        writer.Key("client").Value(FerlClient.Id);
        writer.EndObject();
    }

    public string ToJson()
    {
        var writer = new FerlJsonWriter();
        WriteJson(writer);
        return writer.ToString();
    }
}

// One discrete scene edit in preference_rl's Edit.to_dict() form (a displacement or an
// attribute toggle), plus a carried-object change the bridge keeps as trace metadata only.
public sealed class SceneEditData
{
    public string objectId;
    public double[] displacement;   // null unless this is a displacement
    public double? yaw;             // turn about ROS z (radians) that came with a displacement
    public string attribute;        // null unless this is a toggle
    public bool? carried;           // null unless this is a carried-object change

    public string Kind
    {
        get
        {
            if (displacement != null) return "displace";
            if (attribute != null) return "toggle:" + attribute;
            return carried.HasValue ? "carry" : "unknown";
        }
    }

    public void WriteJson(FerlJsonWriter writer)
    {
        writer.BeginObject();
        writer.Key("object_id").Value(objectId);
        writer.Key("displacement");
        if (displacement != null)
            writer.Vector(displacement[0], displacement[1], displacement[2]);
        else
            writer.Null();
        if (yaw.HasValue)
            writer.Key("yaw").Value(yaw.Value);
        writer.Key("attribute").Value(attribute);
        if (carried.HasValue)
            writer.Key("carried").Value(carried.Value);
        writer.EndObject();
    }
}
