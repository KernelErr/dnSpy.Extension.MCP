using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Provides embedded BepInEx documentation resources for MCP.
	/// </summary>
	[Export(typeof(BepInExResources))]
	sealed class BepInExResources {
		readonly McpSettings settings;
		readonly Dictionary<string, string> resources = new Dictionary<string, string>();

		[ImportingConstructor]
		public BepInExResources(McpSettings settings) {
			this.settings = settings;
			InitializeResources();
		}

		void InitializeResources() {
			// Plugin Structure and Basics
			resources["bepinex://docs/plugin-structure"] = @"# BepInEx Plugin Structure (v6.0.0-pre.1)

## Basic Plugin Template

```csharp
using BepInEx;

namespace MyFirstPlugin
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($""Plugin {PluginInfo.PLUGIN_GUID} is loaded!"");
        }
    }
}
```

## Key Components

### 1. BepInPlugin Attribute (REQUIRED)
Without this attribute, BepInEx will ignore your plugin!

```csharp
[BepInPlugin(""org.bepinex.plugins.exampleplugin"", ""Example Plug-In"", ""1.0.0.0"")]
public class ExamplePlugin : BaseUnityPlugin
```

**Parameters:**
- **GUID**: Unique identifier (use reverse domain notation, e.g., ""com.author.pluginname"")
- **Name**: Human-readable plugin name
- **Version**: Must follow semver format (e.g., ""1.0.0"")

### 2. BaseUnityPlugin
Inherits from UnityEngine.MonoBehaviour, so you can use Unity lifecycle methods:
- `Awake()` - Called when plugin loads
- `Start()` - Called after all Awake methods
- `Update()` - Called every frame
- `OnDestroy()` - Called when plugin unloads

### 3. Logger
Built-in logging system:
```csharp
Logger.LogInfo(""Information message"");
Logger.LogWarning(""Warning message"");
Logger.LogError(""Error message"");
Logger.LogDebug(""Debug message"");
```

## Plugin Metadata Attributes

### Dependencies
```csharp
// Hard dependency (required)
[BepInDependency(""com.bepinex.plugin.required"")]

// Soft dependency (optional)
[BepInDependency(""com.bepinex.plugin.optional"", BepInDependency.DependencyFlags.SoftDependency)]

// Version-specific dependency
[BepInDependency(""com.bepinex.plugin.versioned"", ""~1.2"")]
```

### Process Filtering
```csharp
[BepInProcess(""GameName.exe"")]
[BepInProcess(""AnotherGame.exe"")]
```

### Incompatibilities
```csharp
[BepInIncompatibility(""some.conflicting.plugin"")]
```
";

			// Runtime Patching with Harmony
			resources["bepinex://docs/harmony-patching"] = @"# Runtime Patching with HarmonyX

BepInEx uses HarmonyX for runtime method patching. HarmonyX allows you to modify game methods without permanently changing game files.

## Basic Harmony Usage

### 1. Initialize Harmony
```csharp
using HarmonyLib;

[BepInPlugin(""com.example.myplugin"", ""My Plugin"", ""1.0.0"")]
public class MyPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        var harmony = new Harmony(""com.example.myplugin"");
        harmony.PatchAll(); // Automatically patches all [HarmonyPatch] methods
    }
}
```

### 2. Prefix Patches (Run Before Original Method)
```csharp
[HarmonyPatch(typeof(PlayerController), ""TakeDamage"")]
class TakeDamage_Patch
{
    static bool Prefix(PlayerController __instance, int damage)
    {
        // __instance is the 'this' reference
        // Return false to skip original method
        // Return true to run original method

        if (damage > 100)
        {
            Plugin.Logger.LogInfo(""Prevented lethal damage!"");
            return false; // Skip original method
        }
        return true; // Run original method
    }
}
```

### 3. Postfix Patches (Run After Original Method)
```csharp
[HarmonyPatch(typeof(PlayerController), ""GetHealth"")]
class GetHealth_Patch
{
    static void Postfix(ref int __result)
    {
        // __result is the return value
        // You can modify it before it's returned

        __result *= 2; // Double the health
    }
}
```

### 4. Accessing Private Fields
```csharp
[HarmonyPatch(typeof(EnemyAI), ""Update"")]
class EnemyAI_Patch
{
    static void Prefix(EnemyAI __instance, ref float ___moveSpeed)
    {
        // ___fieldName accesses private fields (three underscores!)
        ___moveSpeed = 10f; // Modify enemy speed
    }
}
```

## Common Patterns

### Patching Methods with Overloads
```csharp
[HarmonyPatch(typeof(ItemManager), ""AddItem"", new Type[] { typeof(string), typeof(int) })]
class AddItem_Patch
{
    static void Prefix(string itemName, int count)
    {
        Plugin.Logger.LogInfo($""Adding {count}x {itemName}"");
    }
}
```

### Patching Properties
```csharp
// Patch getter
[HarmonyPatch(typeof(Player), nameof(Player.MaxHealth), MethodType.Getter)]
class MaxHealth_Getter_Patch
{
    static void Postfix(ref int __result)
    {
        __result = 999; // Unlimited health
    }
}

// Patch setter
[HarmonyPatch(typeof(Player), nameof(Player.MaxHealth), MethodType.Setter)]
class MaxHealth_Setter_Patch
{
    static void Prefix(ref int value)
    {
        value = Math.Max(value, 100); // Minimum health
    }
}
```

### Conditional Patching
```csharp
static bool Prefix(PlayerController __instance)
{
    if (SomeCondition)
    {
        // Do your logic
        return false; // Skip original
    }
    return true; // Run original
}
```

## Parameter Injection

Harmony can inject special parameters:
- `__instance` - The instance object (for non-static methods)
- `__result` - The return value (Postfix only, use `ref`)
- `__state` - Pass data from Prefix to Postfix
- `___fieldName` - Access private field (three underscores!)
- `__args` - All arguments as object array
- Original method parameters by name

## Best Practices

1. **Always use unique Harmony IDs** (usually your plugin GUID)
2. **Use Prefix `return false`** to completely skip original method
3. **Use Postfix** to modify return values or run code after
4. **Keep patches simple** - complex logic should be in separate methods
5. **Log your patches** for debugging
6. **Unpatch on disable**: `harmony.UnpatchSelf()`
";

			// Configuration Guide
			resources["bepinex://docs/configuration"] = @"# BepInEx Configuration System

## Creating Configuration

```csharp
using BepInEx.Configuration;

[BepInPlugin(""com.example.myplugin"", ""My Plugin"", ""1.0.0"")]
public class MyPlugin : BaseUnityPlugin
{
    // Configuration entries
    private ConfigEntry<bool> enableFeature;
    private ConfigEntry<int> maxValue;
    private ConfigEntry<float> multiplier;
    private ConfigEntry<string> playerName;

    private void Awake()
    {
        // Bind configuration entries
        enableFeature = Config.Bind(""General"", ""EnableFeature"", true,
            ""Enable or disable the main feature"");

        maxValue = Config.Bind(""General"", ""MaxValue"", 100,
            new ConfigDescription(""Maximum allowed value"",
            new AcceptableValueRange<int>(1, 1000)));

        multiplier = Config.Bind(""Gameplay"", ""DamageMultiplier"", 1.5f,
            ""Damage multiplier for all attacks"");

        playerName = Config.Bind(""Player"", ""Name"", ""DefaultName"",
            ""Player display name"");

        // Use configuration
        if (enableFeature.Value)
        {
            Logger.LogInfo($""Feature enabled! Max value: {maxValue.Value}"");
        }
    }
}
```

## ConfigEntry Usage

### Accessing Values
```csharp
// Read value
int currentValue = maxValue.Value;

// Set value (triggers save)
maxValue.Value = 50;
```

### Acceptable Values
```csharp
// Range constraint
new AcceptableValueRange<int>(1, 100)

// List constraint
new AcceptableValueList<string>(""Option1"", ""Option2"", ""Option3"")
```

### Change Events
```csharp
enableFeature.SettingChanged += (sender, args) =>
{
    Logger.LogInfo($""Setting changed to: {enableFeature.Value}"");
};
```

## Configuration File

Config files are saved to: `BepInEx/config/com.example.myplugin.cfg`

Example generated file:
```ini
[General]
## Enable or disable the main feature
# Setting type: Boolean
# Default value: true
EnableFeature = true

## Maximum allowed value
# Setting type: Int32
# Default value: 100
# Acceptable value range: From 1 to 1000
MaxValue = 100
```
";

			// Common Scenarios
			resources["bepinex://docs/common-scenarios"] = @"# Common BepInEx Plugin Scenarios

## 1. Modifying Player Stats

```csharp
[HarmonyPatch(typeof(Player), ""Start"")]
class Player_Start_Patch
{
    static void Postfix(Player __instance)
    {
        // Increase player health
        __instance.maxHealth = 200;
        __instance.currentHealth = 200;

        // Increase movement speed
        __instance.moveSpeed = 10f;
    }
}
```

## 2. Unlocking All Items

```csharp
[HarmonyPatch(typeof(ItemDatabase), ""IsItemUnlocked"")]
class ItemDatabase_IsUnlocked_Patch
{
    static void Postfix(ref bool __result)
    {
        __result = true; // All items unlocked
    }
}
```

## 3. Adding Debug Commands

```csharp
private void Update()
{
    if (Input.GetKeyDown(KeyCode.F1))
    {
        GiveAllItems();
    }

    if (Input.GetKeyDown(KeyCode.F2))
    {
        TeleportPlayer(new Vector3(0, 0, 0));
    }
}

void GiveAllItems()
{
    var inventory = Player.instance.inventory;
    foreach (var item in ItemDatabase.allItems)
    {
        inventory.AddItem(item);
    }
}
```

## 4. Logging Game Information

```csharp
[HarmonyPatch(typeof(GameManager), ""LoadLevel"")]
class GameManager_LoadLevel_Patch
{
    static void Prefix(string levelName)
    {
        Plugin.Logger.LogInfo($""Loading level: {levelName}"");
    }
}
```

## 5. Custom UI with Unity

```csharp
private void OnGUI()
{
    if (GUI.Button(new Rect(10, 10, 100, 30), ""Click Me""))
    {
        Logger.LogInfo(""Button clicked!"");
    }

    GUI.Label(new Rect(10, 50, 200, 30), $""Health: {Player.instance.health}"");
}
```

## 6. Preventing Method Execution

```csharp
[HarmonyPatch(typeof(Enemy), ""Attack"")]
class Enemy_Attack_Patch
{
    static bool Prefix()
    {
        // Return false to completely prevent enemy attacks
        return false;
    }
}
```

## 7. Modifying Method Arguments

```csharp
[HarmonyPatch(typeof(DamageHandler), ""ApplyDamage"")]
class DamageHandler_ApplyDamage_Patch
{
    static void Prefix(ref int damage)
    {
        // Reduce all damage by 50%
        damage = (int)(damage * 0.5f);
    }
}
```

## 8. Saving Custom Data

```csharp
private void SaveData()
{
    var dataPath = Path.Combine(Paths.ConfigPath, ""myplugin_data.json"");
    var data = new MyData { score = 100, level = 5 };
    File.WriteAllText(dataPath, JsonUtility.ToJson(data));
}

private MyData LoadData()
{
    var dataPath = Path.Combine(Paths.ConfigPath, ""myplugin_data.json"");
    if (File.Exists(dataPath))
    {
        return JsonUtility.FromJson<MyData>(File.ReadAllText(dataPath));
    }
    return new MyData();
}
```
";

			// IL2CPP Specific Guide
			resources["bepinex://docs/il2cpp-guide"] = @"# BepInEx IL2CPP Plugin Development Guide

## Unity IL2CPP vs Mono: Key Differences

### Plugin Base Class

**Mono:**
```csharp
using BepInEx;

[BepInPlugin(""com.example.plugin"", ""My Plugin"", ""1.0.0"")]
public class Plugin : BaseUnityPlugin  // Inherits MonoBehaviour
{
    private void Awake() { }  // Unity lifecycle method
}
```

**IL2CPP:**
```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;  // IL2CPP-specific namespace

[BepInPlugin(""com.example.plugin"", ""My Plugin"", ""1.0.0"")]
public class Plugin : BasePlugin  // NOT MonoBehaviour
{
    public override void Load() { }  // BepInEx load method, NOT Awake
}
```

### Key Differences Table

| Feature | Mono | IL2CPP |
|---------|------|--------|
| Base Class | `BaseUnityPlugin` | `BasePlugin` |
| Namespace | `BepInEx` | `BepInEx.Unity.IL2CPP` |
| Entry Point | `Awake()` | `Load()` |
| MonoBehaviour | Yes (is MonoBehaviour) | No (separate system) |
| Logger | `Logger` | `Log` |
| Update Loop | `Update()` method | Must add MonoBehaviour manually |
| Harmony | HarmonyX | HarmonyX (same) |

## IL2CPP Plugin Structure

### Basic Template
```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;

namespace MyIL2CPPPlugin
{
    [BepInPlugin(""com.example.myplugin"", ""My IL2CPP Plugin"", ""1.0.0"")]
    public class Plugin : BasePlugin
    {
        public static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo(""Plugin loaded!"");

            // Apply Harmony patches
            var harmony = new Harmony(""com.example.myplugin"");
            harmony.PatchAll();

            // Add custom MonoBehaviour if needed
            AddComponent<MyBehaviour>();
        }
    }
}
```

## Working with IL2CPP Types

### ClassInjector - Registering Custom Types
IL2CPP requires explicit type registration for C# types you want to use:

```csharp
using UnhollowerRuntimeLib;

public override void Load()
{
    // Register custom MonoBehaviour type
    ClassInjector.RegisterTypeInIl2Cpp<MyCustomBehaviour>();

    // Now you can use it
    var go = new GameObject(""MyObject"");
    go.AddComponent<MyCustomBehaviour>();
}

// Custom MonoBehaviour must have IntPtr constructor
public class MyCustomBehaviour : MonoBehaviour
{
    public MyCustomBehaviour(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        // Your update logic
    }
}
```

### IntPtr Constructor Requirement
**ALL** IL2CPP types you create must have this constructor:

```csharp
public class MyClass : SomeIL2CPPType
{
    // REQUIRED for IL2CPP
    public MyClass(IntPtr ptr) : base(ptr) { }

    // Your custom constructors
    public MyClass() : base(ClassInjector.DerivedConstructorPointer<MyClass>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }
}
```

## Adding MonoBehaviour Components

### Method 1: Using AddComponent Helper
```csharp
using BepInEx.Unity.IL2CPP.Utils.Collections;

public override void Load()
{
    // Register first
    ClassInjector.RegisterTypeInIl2Cpp<GameManager>();

    // Add to existing GameObject
    var manager = Camera.main.gameObject.AddComponent<GameManager>();

    // Or create new GameObject
    var go = new GameObject(""Manager"");
    go.AddComponent<GameManager>();
    GameObject.DontDestroyOnLoad(go);
}

public class GameManager : MonoBehaviour
{
    public GameManager(IntPtr ptr) : base(ptr) { }

    void Awake()
    {
        Plugin.Logger.LogInfo(""GameManager awake"");
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F5))
        {
            Plugin.Logger.LogInfo(""F5 pressed!"");
        }
    }
}
```

### Method 2: Coroutines in IL2CPP
```csharp
using System.Collections;
using MelonLoader.TinyJSON;

public class TimerBehaviour : MonoBehaviour
{
    public TimerBehaviour(IntPtr ptr) : base(ptr) { }

    void Start()
    {
        StartCoroutine(MyCoroutine().WrapToIl2Cpp());
    }

    private IEnumerator MyCoroutine()
    {
        Plugin.Logger.LogInfo(""Coroutine started"");
        yield return new WaitForSeconds(5f);
        Plugin.Logger.LogInfo(""5 seconds passed"");
    }
}
```

## IL2CPP Type Conversions

### Il2CppSystem Types
IL2CPP uses special types from the `Il2CppSystem` namespace:

```csharp
// String conversion
string monoString = ""Hello"";
Il2CppSystem.String il2cppString = monoString;

// List conversion
using Il2CppSystem.Collections.Generic;

List<string> monoList = new List<string> { ""A"", ""B"" };
List<Il2CppSystem.String> il2cppList = new List<Il2CppSystem.String>();
foreach (var item in monoList)
{
    il2cppList.Add(item);
}

// Action/Func delegates
Il2CppSystem.Action action = new System.Action(() =>
{
    Plugin.Logger.LogInfo(""Action called"");
}).Cast<Il2CppSystem.Action>();
```

### Casting and Type Checking
```csharp
// Check if IL2CPP object is certain type
if (obj.TryCast<Player>() != null)
{
    var player = obj.Cast<Player>();
    player.health = 100;
}

// Safe casting
var player = obj.TryCast<Player>();
if (player != null)
{
    // Use player
}
```

## Harmony Patching in IL2CPP

Harmony works the same way, but be careful with types:

```csharp
[HarmonyPatch(typeof(PlayerController), ""TakeDamage"")]
class TakeDamage_Patch
{
    static bool Prefix(PlayerController __instance, float damage)
    {
        // IL2CPP types work the same
        Plugin.Logger.LogInfo($""Player taking {damage} damage"");
        return true;
    }
}

[HarmonyPatch(typeof(ItemManager), ""AddItem"")]
class AddItem_Patch
{
    static void Postfix(Il2CppSystem.String itemName, int count)
    {
        // Note: IL2CPP string type
        string monoName = itemName; // Auto-converts
        Plugin.Logger.LogInfo($""Added {count}x {monoName}"");
    }
}
```

## Common IL2CPP Patterns

### Accessing Game Objects
```csharp
using UnityEngine;

// Find objects
var player = GameObject.Find(""Player"");
var players = GameObject.FindObjectsOfType<Player>();

// Access components
var rigidbody = player.GetComponent<Rigidbody>();
var allComponents = player.GetComponents<Component>();
```

### Event Subscriptions
```csharp
// Subscribe to IL2CPP events
gameManager.OnGameStart += new System.Action(() =>
{
    Plugin.Logger.LogInfo(""Game started!"");
}).Cast<Il2CppSystem.Action>();
```

### Working with Collections
```csharp
using Il2CppSystem.Collections.Generic;

// Iterate IL2CPP list
List<Enemy> enemies = EnemyManager.GetEnemies();
foreach (var enemy in enemies)
{
    enemy.health = 0;
}

// Convert to Mono enumerable
var monoList = enemies.ToArray(); // Now you can use LINQ
var weakEnemies = monoList.Where(e => e.health < 50);
```

## Debugging IL2CPP Plugins

### Logging
```csharp
public override void Load()
{
    Log.LogInfo(""Info message"");
    Log.LogWarning(""Warning message"");
    Log.LogError(""Error message"");
    Log.LogDebug(""Debug message"");
}
```

### Finding Types and Methods
```csharp
// Log all loaded types
Log.LogInfo(""Finding Player types..."");
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
{
    foreach (var type in assembly.GetTypes())
    {
        if (type.Name.Contains(""Player""))
        {
            Log.LogInfo($""Found: {type.FullName}"");
        }
    }
}
```

## Project Setup for IL2CPP

### .csproj Configuration
```xml
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>MyIL2CPPPlugin</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""BepInEx.Unity.IL2CPP"" Version=""6.0.0-pre.1"" />
    <PackageReference Include=""BepInEx.PluginInfoProps"" Version=""2.1.0"" />
  </ItemGroup>
</Project>
```

### Required References
- `BepInEx.Unity.IL2CPP.dll`
- `BepInEx.Core.dll`
- Game's unhollowed assemblies (in `BepInEx/unhollowed/`)

## Summary: Quick Reference

**Creating IL2CPP Plugin:**
1. Inherit from `BasePlugin` (not `BaseUnityPlugin`)
2. Use `Load()` method (not `Awake()`)
3. Use `Log` property (not `Logger`)

**Custom Types:**
1. Register with `ClassInjector.RegisterTypeInIl2Cpp<T>()`
2. Add IntPtr constructor
3. Call `ClassInjector.DerivedConstructorPointer<T>()` in custom constructor

**MonoBehaviour:**
1. Register type with ClassInjector
2. Use `AddComponent<T>()` to add to GameObject
3. Implement IntPtr constructor

**Type Conversions:**
- Use `Il2CppSystem` types for IL2CPP interop
- Strings auto-convert between mono/IL2CPP
- Use `.Cast<T>()` for explicit conversions
";

			// Mono vs IL2CPP Comparison
			resources["bepinex://docs/mono-vs-il2cpp"] = @"# BepInEx: Mono vs IL2CPP Side-by-Side Comparison

## Quick Identification

**How to identify your game type:**

1. **Check game folder:**
   - Mono: `GameName_Data/Managed/Assembly-CSharp.dll` exists
   - IL2CPP: `GameName_Data/il2cpp_data/` folder exists

2. **Check executable:**
   - Mono: One .exe file
   - IL2CPP: .exe + `GameAssembly.dll`

3. **BepInEx detection:**
   - Run BepInEx once, it will detect and log the game type

## Plugin Structure Comparison

### File: Plugin.cs

#### Mono
```csharp
using BepInEx;
using UnityEngine;

namespace MyPlugin
{
    [BepInPlugin(""com.example.plugin"", ""My Plugin"", ""1.0.0"")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo(""Plugin loaded!"");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                Logger.LogInfo(""F1 pressed"");
            }
        }
    }
}
```

#### IL2CPP
```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace MyPlugin
{
    [BepInPlugin(""com.example.plugin"", ""My Plugin"", ""1.0.0"")]
    public class Plugin : BasePlugin
    {
        public override void Load()
        {
            Log.LogInfo(""Plugin loaded!"");

            // Must register and add MonoBehaviour manually
            ClassInjector.RegisterTypeInIl2Cpp<MyBehaviour>();
            var go = new GameObject(""MyPlugin"");
            go.AddComponent<MyBehaviour>();
            GameObject.DontDestroyOnLoad(go);
        }
    }

    // Separate MonoBehaviour class
    public class MyBehaviour : MonoBehaviour
    {
        public MyBehaviour(IntPtr ptr) : base(ptr) { }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                Plugin.Log.LogInfo(""F1 pressed"");
            }
        }
    }
}
```

## Feature-by-Feature Comparison

### 1. Project Setup

| Aspect | Mono | IL2CPP |
|--------|------|--------|
| **Template** | `dotnet new bep6plugin_unitymono` | `dotnet new bep6plugin_il2cpp` |
| **Target Framework** | `net35`, `net46`, or `netstandard2.0` | `net6.0` |
| **NuGet Package** | `BepInEx.Unity.Mono` | `BepInEx.Unity.IL2CPP` |
| **Game Assemblies** | `GameName_Data/Managed/*.dll` | `BepInEx/unhollowed/*.dll` |

### 2. Plugin Base Class

| Aspect | Mono | IL2CPP |
|--------|------|--------|
| **Base Class** | `BaseUnityPlugin` | `BasePlugin` |
| **Inheritance** | Inherits `MonoBehaviour` | Does NOT inherit `MonoBehaviour` |
| **Namespace** | `using BepInEx;` | `using BepInEx.Unity.IL2CPP;` |

### 3. Initialization

| Aspect | Mono | IL2CPP |
|--------|------|--------|
| **Entry Method** | `void Awake()` | `void Load()` (override) |
| **Lifecycle** | Unity lifecycle methods work | Must add MonoBehaviour manually |
| **Logger** | `Logger.LogInfo()` | `Log.LogInfo()` |

### 4. MonoBehaviour Usage

#### Mono - Built-in
```csharp
[BepInPlugin(""..."", ""..."", ""..."")]
public class Plugin : BaseUnityPlugin  // IS a MonoBehaviour
{
    void Awake() { }     // ✓ Works
    void Start() { }     // ✓ Works
    void Update() { }    // ✓ Works
    void OnGUI() { }     // ✓ Works
}
```

#### IL2CPP - Manual Setup Required
```csharp
[BepInPlugin(""..."", ""..."", ""..."")]
public class Plugin : BasePlugin  // NOT a MonoBehaviour
{
    void Awake() { }   // ✗ Never called
    void Update() { }  // ✗ Never called

    public override void Load()
    {
        // Must create separate MonoBehaviour
        ClassInjector.RegisterTypeInIl2Cpp<MyBehaviour>();
        AddComponent<MyBehaviour>();
    }
}

public class MyBehaviour : MonoBehaviour
{
    public MyBehaviour(IntPtr ptr) : base(ptr) { }  // Required!

    void Update() { }  // ✓ Now this works
}
```

### 5. Custom Types

#### Mono - Direct Usage
```csharp
// Just create the class
public class MyData
{
    public string name;
    public int value;
}

// Use it
var data = new MyData { name = ""test"", value = 42 };
```

#### IL2CPP - Registration Required
```csharp
// Must register if inheriting IL2CPP types
public class MyComponent : MonoBehaviour
{
    // REQUIRED constructor
    public MyComponent(IntPtr ptr) : base(ptr) { }
}

// Register before use
public override void Load()
{
    ClassInjector.RegisterTypeInIl2Cpp<MyComponent>();

    // Now can use
    var go = new GameObject();
    go.AddComponent<MyComponent>();
}
```

### 6. Type System

#### Mono - Standard .NET
```csharp
string text = ""Hello"";
List<string> list = new List<string>();
Action callback = () => Debug.Log(""Called"");
```

#### IL2CPP - Dual Type System
```csharp
// Mono types (C#)
string monoText = ""Hello"";
System.Collections.Generic.List<string> monoList;

// IL2CPP types (from game)
Il2CppSystem.String il2cppText;
Il2CppSystem.Collections.Generic.List<Il2CppSystem.String> il2cppList;

// Conversions
Il2CppSystem.String converted = monoText;  // Implicit
string back = il2cppText;                   // Implicit

// Delegates need casting
Il2CppSystem.Action action = new System.Action(() => {
    Debug.Log(""Called"");
}).Cast<Il2CppSystem.Action>();
```

### 7. Harmony Patching

#### Mono - Straightforward
```csharp
[HarmonyPatch(typeof(Player), ""TakeDamage"")]
class Patch
{
    static void Prefix(Player __instance, int damage)
    {
        // Types match directly
    }
}
```

#### IL2CPP - Mind the Types
```csharp
[HarmonyPatch(typeof(Player), ""TakeDamage"")]
class Patch
{
    static void Prefix(Player __instance, float damage)
    {
        // Parameters might use Il2CppSystem types
        // Check dnSpy for exact signatures
    }
}
```

### 8. Configuration

#### Both - Same API
```csharp
// Mono
var config = Config.Bind(""General"", ""Setting"", true);

// IL2CPP
var config = Config.Bind(""General"", ""Setting"", true);

// API is identical
```

## Common Pitfalls

### Mono Pitfalls
```csharp
// ✗ Forgetting to check if types exist
var type = Type.GetType(""GameNamespace.Player"");
type.GetMethod(""Heal"");  // NullReferenceException if type is null

// ✓ Always null-check
if (type != null)
{
    var method = type.GetMethod(""Heal"");
    if (method != null) { ... }
}
```

### IL2CPP Pitfalls
```csharp
// ✗ Using MonoBehaviour methods without registration
public class Plugin : BasePlugin
{
    void Update() { }  // NEVER CALLED! Plugin is not MonoBehaviour
}

// ✓ Register and add component
public override void Load()
{
    ClassInjector.RegisterTypeInIl2Cpp<UpdateHandler>();
    AddComponent<UpdateHandler>();
}

// ✗ Missing IntPtr constructor
public class MyComponent : MonoBehaviour
{
    public MyComponent() { }  // IL2CPP will crash!
}

// ✓ Always add IntPtr constructor
public class MyComponent : MonoBehaviour
{
    public MyComponent(IntPtr ptr) : base(ptr) { }
}

// ✗ Forgetting to cast delegates
button.onClick.AddListener(OnClick);  // May not work

// ✓ Cast delegates properly
button.onClick.AddListener(
    new System.Action(OnClick).Cast<Il2CppSystem.Action>()
);
```

## Decision Guide

### Use Mono when:
- ✓ Game uses Mono backend (most older Unity games)
- ✓ You want simpler development
- ✓ You don't need advanced IL2CPP features

### Use IL2CPP when:
- ✓ Game uses IL2CPP backend (newer Unity games, mobile)
- ✓ No choice - Mono won't work on IL2CPP games
- ✓ Game explicitly requires it

**Note:** You cannot mix them - use what your game requires!

## Migration Checklist (Mono → IL2CPP)

If porting a Mono plugin to IL2CPP:

- [ ] Change `BaseUnityPlugin` to `BasePlugin`
- [ ] Change `Awake()` to `Load()` (and make it override)
- [ ] Change `Logger` to `Log`
- [ ] Extract Update/OnGUI to separate MonoBehaviour class
- [ ] Add IntPtr constructor to all MonoBehaviour classes
- [ ] Register custom types with ClassInjector
- [ ] Convert System types to Il2CppSystem where needed
- [ ] Cast delegates to Il2CppSystem types
- [ ] Update project file (target framework, packages)
- [ ] Reference unhollowed assemblies instead of Managed
";

			settings.Log($"Initialized {resources.Count} BepInEx documentation resources");
		}

		public List<ResourceInfo> GetResources() {
			var resourceList = new List<ResourceInfo>();

			foreach (var kvp in resources) {
				var segments = kvp.Key.Split('/');
				var name = segments[segments.Length - 1];

				resourceList.Add(new ResourceInfo {
					Uri = kvp.Key,
					Name = name,
					Description = $"BepInEx v6 Documentation: {FormatName(name)}",
					MimeType = "text/markdown"
				});
			}

			return resourceList;
		}

		public string? ReadResource(string uri) {
			return resources.TryGetValue(uri, out var content) ? content : null;
		}

		string FormatName(string name) {
			// Convert kebab-case to Title Case
			return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
				name.Replace('-', ' ')
			);
		}
	}
}
