using Godot;

namespace AutoAnthonyCardTinkering;

/// <summary>Persistent settings shared by the normal editor and optional Create Cards UI.</summary>
internal static class TinkeringSettings
{
    private const string Path = "user://auto_anthony_card_tinkering.cfg";
    private const string Section = "card_tinkering";
    private const string FreeformKey = "freeform_editing";
    private const string IgnoreCapacityKey = "ignore_capacity_limit";
    private static bool _loaded;
    private static bool _freeformEnabled;
    private static bool _ignoreCapacityLimit;

    internal static bool FreeformEnabled
    {
        get
        {
            EnsureLoaded();
            return _freeformEnabled;
        }
        set
        {
            EnsureLoaded();
            if (_freeformEnabled == value) return;
            _freeformEnabled = value;
            Save(FreeformKey, value);
        }
    }

    internal static bool IgnoreCapacityLimit
    {
        get
        {
            EnsureLoaded();
            return _ignoreCapacityLimit;
        }
        set
        {
            EnsureLoaded();
            if (_ignoreCapacityLimit == value) return;
            _ignoreCapacityLimit = value;
            Save(IgnoreCapacityKey, value);
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var config = new ConfigFile();
        if (config.Load(Path) != Error.Ok) return;
        _freeformEnabled = config.GetValue(Section, FreeformKey, false).AsBool();
        _ignoreCapacityLimit = config.GetValue(Section, IgnoreCapacityKey, false).AsBool();
    }

    private static void Save(string key, bool value)
    {
        var config = new ConfigFile();
        config.Load(Path);
        config.SetValue(Section, key, value);
        config.Save(Path);
    }
}
