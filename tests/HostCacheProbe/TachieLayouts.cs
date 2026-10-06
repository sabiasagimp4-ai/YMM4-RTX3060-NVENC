using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

// Where a tachie fixture's items are drawn: as they are ("root"), under a group control that moves them ("group"), in a
// composite group (drawn by the group's own TimelineSource, "composite"), or in another scene shown by a scene item of a
// new root timeline ("scene").
internal static class TachieLayouts
{
    internal static readonly string[] Nested = ["group", "composite", "scene"];

    // The scene to render: the fixture's own, or the new root of "scene".
    internal static Scene Apply(string layout, Timeline timeline, Scene scene)
    {
        switch (layout)
        {
            case "root":
                return scene;
            case "group":
            case "composite":
                {
                    var items = timeline.Items.ToArray();
                    foreach (var item in items) item.Layer++;
                    var group = new GroupItem { Frame = 0, Length = timeline.Length, Layer = 0, GroupRange = items.Max(item => item.Layer) };
                    group.X.SetFirst(9);
                    if (layout == "composite") Composite(group);
                    timeline.Items = timeline.Items.Insert(0, group);
                    timeline.RefreshTimelineLengthAndMaxLayer();
                    return scene;
                }
            case "scene":
                {
                    var root = new Timeline();
                    root.VideoInfo.Width = timeline.VideoInfo.Width; root.VideoInfo.Height = timeline.VideoInfo.Height; root.VideoInfo.FPS = timeline.VideoInfo.FPS;
                    var item = new SceneItem { Frame = 0, Length = timeline.Length, Layer = 0, SceneId = timeline.ID };
                    item.X.SetFirst(-7);
                    root.Items = [item];
                    root.RefreshTimelineLengthAndMaxLayer();
                    scene.Scenes.AddScene(root);
                    return new Scene(root, scene.Scenes, []);
                }
            default:
                throw new ArgumentException("Unknown tachie layout " + layout, nameof(layout));
        }
    }

    // GroupItem.IsComposite is newer than the oldest builds the probe starts on.
    private static void Composite(GroupItem group) => group.IsComposite = true;
}
