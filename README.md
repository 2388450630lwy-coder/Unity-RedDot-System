# RedDot 红点系统技术文档

> **版本**：2.0  
> **Unity 版本**：2022.3.17f1  
> **语言**：C#（.NET Standard 2.1）  
> **核心特性**：零运行时字符串分配、O(depth) 增量祖先传播、64-bit 碰撞免疫、编辑器全可视化

---

## 目录

1. [架构概览](#1-架构概览)
2. [核心概念](#2-核心概念)
3. [数据层 — RedDotDataStore](#3-数据层--reddotdatastore)
4. [路由层 — RedDotTrie](#4-路由层--reddottrie)
5. [哈希 — RedDotHash](#5-哈希--reddothash)
6. [门面层 — RedDotManager](#6-门面层--reddotmanager)
7. [状态与类型 — RedDotState / RedDotType](#7-状态与类型--reddotstate--reddottype)
8. [路径定义 — RedDotPathDefinition](#8-路径定义--reddotpathdefinition)
9. [代码生成](#9-代码生成)
10. [UI 绑定 — RedDotBinder](#10-ui-绑定--reddotbinder)
11. [编辑器工具](#11-编辑器工具)
12. [测试与调试](#12-测试与调试)
13. [完整 API 参考](#13-完整-api-参考)
14. [最佳实践](#14-最佳实践)

---

## 1. 架构概览

```
┌─────────────────────────────────────────────────────────────────┐
│                        业务层 (Gameplay)                        │
│   RedDotManager.Instance.SetRedDot(hash, count, type)           │
│   RedDotManager.Instance.AddListener(hash, callback)            │
└───────────────────────────┬─────────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────────┐
│                      门面层 (Facade)                             │
│   RedDotManager : MonoBehaviour (Singleton, DontDestroyOnLoad)  │
│   — 协调 Trie + DataStore                                       │
│   — SetRedDot → 增量祖先传播 → 通知监听器                        │
│   — 静态 / 动态节点生命周期管理                                   │
│   — 复合键 (parentHash, childId) 保证动态节点零碰撞路由           │
└──────────────┬────────────────────────────┬─────────────────────┘
               │                            │
┌──────────────▼──────────────┐ ┌───────────▼────────────────────┐
│     路由层 (Routing)         │ │      数据层 (Data)              │
│   RedDotTrie                │ │   RedDotDataStore              │
│   — 前缀树结构               │ │   — 平行数组存储                 │
│   — long hash → nodeIndex   │ │   — SelfCount / TotalCount     │
│   — 父子导航 O(1)            │ │   — SelfType / AggregateType   │
│   — 空槽 free list 复用      │ │   — Listener 管理              │
└─────────────────────────────┘ └────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────────────────────┐
│                    基础设施 (Foundation)                          │
│   RedDotHash (FNV-1a 64-bit)  │  RedDotState (readonly struct)  │
│   RedDotType (bitmask flags)  │  RedDotPathDefinition (SO)      │
└─────────────────────────────────────────────────────────────────┘
```

**核心流程** — `SetRedDot(pathHash, count, type)`：

1. `Trie.FindIndex(pathHash)` → 拿到内部 nodeIndex
2. `DataStore.SetSelfCount(nodeIndex, count)` → 返回 delta
3. `while (parent != INVALID)` 沿父链上溯，每层 `DataStore.AddDeltaToTotal(parent, delta)` + `RefreshNodeFlags(parent)`
4. 每个变化节点 `NotifyNode → NotifyListeners`

**复杂度**：O(depth)，与节点总数无关。

---

## 2. 核心概念

### 2.1 路径与 Hash

系统的标识符不是字符串，而是 **FNV-1a 64-bit 哈希值**（`long`）。

```
字符串路径    →    RedDotHash.Compute()    →    long hash（存储和运行时使用）
"Root_Mail_System"           ↓                  0xA8B3C4D5E6F70123
                     RedDotPaths.Root_Mail_System（编译期常量）
```

- **编辑期**：`RedDotPathEditor` 生成 `RedDotPaths.cs`，将哈希硬编码为 `const long`
- **运行期**：所有 API 使用 `long pathHash`，零字符串分配
- **碰撞概率**：64-bit 空间下，十亿条路径的碰撞概率约 0.001%，实际可视为无碰撞

### 2.2 SelfCount vs TotalCount

```
          Root                SelfCount = 0    TotalCount = 8   ← 聚合整棵树
          ├── Mail            SelfCount = 0    TotalCount = 8   ← 聚合 System + Person
          │   ├── System      SelfCount = 3    TotalCount = 3   ← 叶子，Self = Total
          │   └── Person      SelfCount = 5    TotalCount = 5
          └── Shop
              └── Lottery     SelfCount = 0    TotalCount = 1
                  └── Free    SelfCount = 1    TotalCount = 1
```

| 字段 | 含义 | 叶子节点 | 容器节点 |
|------|------|---------|---------|
| **SelfCount** | 自身被 SetRedDot 设置的值 | 直接值 | 直接值，**不**含子节点 |
| **TotalCount** | SelfCount + 所有子孙的 SelfCount | = SelfCount | >= SelfCount |
| **Visible** | `TotalCount > 0` | — | — |

`TotalCount` 是**增量维护**的：`SetRedDot` 算出叶子节点的 delta 后，沿父链逐层 `AddDeltaToTotal`，不做全局扫描。

### 2.3 红点类型 (RedDotType)

```csharp
public enum RedDotType
{
    Normal    = 1 << 0,  // 普通红点
    Tips      = 1 << 1,  // 提示
    CanUpdate = 1 << 2,  // 可升级
    IsNew     = 1 << 3,  // 新获得
    Number    = 1 << 4,  // 带数字
}
```

**优先级**（`RedDotTypeHelper.PriorityOrder`）：

```
IsNew > CanUpdate > Tips > Normal > Number
```

**父节点聚合规则**：父节点的类型 = `自身类型 | 所有子节点类型`（位或）。

```
Hero (CanUpdate | IsNew)          ← 父节点聚合所有子节点类型
├── Hero_New    (IsNew)           ← 子节点1
└── Hero_Upgrade (CanUpdate)      ← 子节点2
```

- `GetEffectiveType(hash)` → 返回**最高优先级**的单个类型（如 `IsNew`）
- `GetActiveTypes(hash)` → 返回**所有活跃类型**的列表

### 2.4 动态节点与复合键

**动态节点** 是运行时创建的节点（如邮件第 N 封、背包第 N 格），由 `(parentHash, childId)` 复合键唯一标识。

```
复合键映射：_dynamicKeyToIndex[(parentHash, childId)] → nodeIndex
```

即使 `ComputeDynamic(parentHash, childId)` 产生哈希碰撞，复合键仍能精确路由到正确节点，**完全消除动态节点碰撞的影响**。

---

## 3. 数据层 — RedDotDataStore

**文件**：`Core/RedDotDataStore.cs`  
**定位**：纯数据容器，不依赖 Unity API

### 3.1 内部结构

```
_capacity = 1024 → 2048 → 4096 ...
         │
    ┌────┴────────────────────────────────────────────┐
    │  _selfCounts[nodeIndex]     int[]               │
    │  _totalCounts[nodeIndex]    int[]               │
    │  _selfTypeFlags[nodeIndex]  RedDotType[]        │
    │  _totalTypeFlags[nodeIndex] RedDotType[]        │
    │  _listeners[nodeIndex]      List<Action<RS>>[]  │
    └─────────────────────────────────────────────────┘
```

所有数组按 `nodeIndex` 并行索引，无字典开销，缓存友好。

### 3.2 关键方法

| 方法 | 返回值 | 说明 |
|------|--------|------|
| `EnsureCapacity(idx)` | void | 不足时 ×2 扩容所有数组 |
| `SetSelfCount(idx, count)` | `int delta` | 写入 SelfCount，返回变化量供祖先传播 |
| `AddDeltaToTotal(idx, delta)` | `bool` | 祖先 TotalCount += delta，返回是否变化 |
| `SetSelfTypeFlag(idx, type)` | `bool` | count=0 时 type 自动清为 0 |
| `SetTypeFlags(idx, flags)` | `bool` | 写入聚合类型（由 Manager 计算） |
| `GetHighestType(idx)` | `RedDotType` | 遍历 PriorityOrder 取首个命中的 |
| `GetActiveTypes(idx, outList)` | void | 所有活跃类型（零分配版） |
| `GetState(idx, pathHash)` | `RedDotState` | 快照 |

### 3.3 监听器管理

```csharp
// 添加（去重）
AddListener(idx, callback) → _listeners[idx].Contains(callback) 则不重复添加

// 通知（倒序遍历，安全处理自注销）
NotifyListeners(idx, state) → for (int i = list.Count-1; i >= 0; i--)
                                try { list[i]?.Invoke(state); }
                                catch (Exception e) { Debug.LogError(e); }

// 删除（直接 Remove，列表空时置 null）
RemoveListener(idx, callback) → list.Remove(callback)
                                if (list.Count == 0) _listeners[idx] = null
```

---

## 4. 路由层 — RedDotTrie

**文件**：`Core/RedDotTrie.cs`  
**数据结构**：基于 `List<TrieNode>` + `Dictionary<long, int>` 的紧凑前缀树

### 4.1 数据结构

```csharp
struct TrieNode {
    int                   ParentIndex;   // -1 = Root
    long                  PathHash;
    Dictionary<long, int> Children;      // null when empty（懒初始化）
}

List<TrieNode>         _nodes;          // Index 0 = Root（PathHash=0, ParentIndex=-1）
Dictionary<long, int>  _pathToIndex;    // pathHash → nodeIndex
Stack<int>             _freeSlots;      // 已删除节点的槽位回收池
```

```
_pathToIndex:  { 0xA8B3C4D5E6F70123 → 3, 0x1F2E3D4C5B6A7980 → 1, ... }

_nodes:
  [0] Root           Parent=-1  Children={...→1, ...→2}
  [1] Root_Mail      Parent=0   Children={...→3, ...→4}
  [2] Root_Bag       Parent=0   Children={...}
  [3] Root_Mail_System  Parent=1  Children=null (leaf)
  [4] Root_Mail_Person  Parent=1  Children=null (leaf)
```

**节点槽复用**：`TryRemoveNode` 将已删除槽位推入 `_freeSlots`；`RegisterNode` 优先从 `_freeSlots` 取槽位，避免 `_nodes` 列表单调增长。

### 4.2 关键方法

| 方法 | 复杂度 | 说明 |
|------|--------|------|
| `RegisterNode(pathHash, parentHash)` | O(1) | 存在则返回；父不存在挂 Root；优先复用空槽 |
| `FindIndex(pathHash)` | O(1) | Dictionary 查找 |
| `GetParentIndex(idx)` | O(1) | 数组索引 |
| `GetChildren(idx, outList)` | O(k) | k = 子节点数，写入调用方 buffer |
| `TryRemoveNode(pathHash)` | O(1) | 仅叶子 + 非 Root 可删；回收槽位 |
| `GetAncestors(idx)` | O(depth) | 上溯直到 Root |

---

## 5. 哈希 — RedDotHash

**文件**：`Core/RedDotHash.cs`  
**算法**：FNV-1a 64-bit

```csharp
public static long Compute(string value)
{
    const ulong OFFSET = 14695981039346656037UL;
    const ulong PRIME  = 1099511628211UL;

    ulong hash = OFFSET;
    for (int i = 0; i < value.Length; i++)
    {
        char c = value[i];
        hash ^= (byte)(c & 0xFF);        hash *= PRIME;
        hash ^= (byte)((c >> 8) & 0xFF); hash *= PRIME;
    }
    return unchecked((long)hash);
}
```

**设计要点**：
- 每个 `char`（UTF-16）按两个 byte（小端序）哈希
- 空字符串返回 0（Root 的特殊值）
- `unchecked` 允许溢出回绕
- 64-bit 空间碰撞概率极低，十亿条路径约 0.001%

### 5.1 动态节点 Hash

```csharp
/// <summary>
/// 用父路径 hash + 业务 ID 计算动态节点 hash，零字符串分配。
/// 如 ComputeDynamic(RedDotPaths.Root_Mail, 1001)。
/// </summary>
public static long ComputeDynamic(long parentHash, int childId)
{
    ulong hash = FNV64_OFFSET_BASIS;
    ulong ph   = unchecked((ulong)parentHash);
    // 对 parentHash 8 字节逐字节 FNV-1a
    hash ^= (byte)(ph & 0xFF);          hash *= FNV64_PRIME;
    // ... (8 轮)
    // 对 childId 4 字节逐字节 FNV-1a
    hash ^= (byte)(childId & 0xFF);     hash *= FNV64_PRIME;
    // ... (4 轮)
    return unchecked((long)hash);
}
```

用于动态节点 hash 计算，零 string、零 byte[]、零装箱。**注意**：动态节点 API 通过复合键 `(parentHash, childId)` 路由，不依赖此 hash 的唯一性。

---

## 6. 门面层 — RedDotManager

**文件**：`Core/RedDotManager.cs`  
**生命周期**：MonoBehaviour 单例，`DontDestroyOnLoad`

### 6.1 单例访问

```csharp
// 获取实例（自动创建）
RedDotManager mgr = RedDotManager.Instance;

// 安全检查（不触发创建）
if (RedDotManager.HasInstance) { ... }
```

### 6.2 路径注册

```csharp
// 方式1：自动注册（推荐）
// EnsureRegistered() 在首次访问 Manager 时自动调用，无需手动触发

// 方式2：手动注册静态路径
mgr.RegisterNode(RedDotPaths.Root_Mail, RedDotPaths.Root, isStatic: true);

// 方式3：注册动态节点
int nodeIndex = mgr.RegisterDynamicNode(RedDotPaths.Root_Mail, 1001);
```

- **静态路径**：`isStatic: true` → 受保护，`RemoveDynamicLeafNode` 拒绝删除
- **动态路径**：`isStatic: false` → 可通过 `RemoveDynamicLeafNode` 删除（叶子 + 无监听器）

### 6.3 SetRedDot 完整流程

```csharp
public void SetRedDot(long pathHash, int count, RedDotType type)
{
    // 1. 校验 & 查找
    if (pathHash == 0L) return;
    EnsureRegistered();
    int nodeIndex = _trie.FindIndex(pathHash);
    if (nodeIndex == INVALID_INDEX) { Warning; return; }

    // 2. 更新自身（内部 SetRedDotByIndex）
    int delta           = _data.SetSelfCount(nodeIndex, count);
    RedDotType selfType = count > 0 ? type : 0;
    bool selfTypeChanged  = _data.SetSelfTypeFlag(nodeIndex, selfType);
    bool totalTypeChanged = RefreshNodeFlags(nodeIndex);
    if (delta != 0 || selfTypeChanged || totalTypeChanged)
        NotifyNode(nodeIndex);

    // 3. 沿祖先链上溯
    int current = _trie.GetParentIndex(nodeIndex);
    while (current != INVALID_INDEX)
    {
        _data.AddDeltaToTotal(current, delta);
        RefreshNodeFlags(current);
        NotifyNode(current);
        current = _trie.GetParentIndex(current);
    }
}
```

### 6.4 查询 API

```csharp
int         total = mgr.GetRedDot(hash);          // TotalCount（含子孙）
int         self  = mgr.GetSelfRedDot(hash);       // SelfCount（仅自身）
RedDotState s     = mgr.GetState(hash);            // 完整快照
RedDotType  t     = mgr.GetEffectiveType(hash);    // 最高优先级类型
```

### 6.5 监听器

```csharp
// 订阅 → 立即回调当前状态
mgr.AddListener(hash, (RedDotState state) => {
    if (state.Visible) ShowRedDot();
    else               HideRedDot();
});

// 取消订阅
mgr.RemoveListener(hash, callback);
```

### 6.6 生命周期管理

```csharp
mgr.ClearNode(pathHash);               // 清零自身 SelfCount（不递归）
mgr.ClearNodeRecursive(pathHash);      // 递归清整棵子树（静态→清零，动态→尝试移除）
mgr.RemoveDynamicLeafNode(pathHash);   // 条件：非静态、无子节点、无监听器
mgr.ResetAll();                        // 清空 Trie + DataStore，重新注册静态路径
```

---

## 7. 状态与类型 — RedDotState / RedDotType

### 7.1 RedDotState

```csharp
public readonly struct RedDotState
{
    public readonly long PathHash;             // 路径哈希（64-bit）
    public readonly int  SelfCount;            // 自身计数
    public readonly int  TotalCount;           // 聚合计数（自身+子孙）
    public readonly RedDotType EffectiveType;  // 最高优先级类型
    public bool Visible => TotalCount > 0;
}
```

- **栈分配**：`readonly struct`，无 GC 压力
- **完整快照**：监听器收到后无需二次查询 Manager

### 7.2 RedDotType

```csharp
public enum RedDotType
{
    Normal    = 1 << 0,  // 普通红点
    Tips      = 1 << 1,  // 提示
    CanUpdate = 1 << 2,  // 可升级
    IsNew     = 1 << 3,  // 新获得
    Number    = 1 << 4,  // 数字角标
}
```

**优先级排序** (`RedDotTypeHelper.PriorityOrder`)：`IsNew > CanUpdate > Tips > Normal > Number`

**类型聚合**：父节点类型 = 自身类型 | 所有子节点类型（位或）

---

## 8. 路径定义 — RedDotPathDefinition

**文件**：`Core/RedDotPathDefinition.cs`  
**类型**：ScriptableObject  
**创建**：`Assets → Create → RedDot → Path Definition`

### 8.1 数据结构

```csharp
public class RedDotPathDefinition : ScriptableObject
{
    public List<RedDotPathEntry> Paths;
    public string ClassName  = "RedDotPaths";
    public string Namespace  = "RedDot";
    public string OutputPath = "Scripts/RedDot/Generated/RedDotPaths.cs";
    public string RegistrationOutputPath = "Scripts/RedDot/Generated/RedDotPathRegistration.cs";
}

public struct RedDotPathEntry
{
    public string Path;    // e.g. "Root_Mail_System"
    public long   Hash;    // FNV-1a 64-bit computed
    public string Comment; // e.g. "系统邮件"
}
```

### 8.2 关键方法

| 方法 | 说明 |
|------|------|
| `RecalculateHashes()` | 对所有 Path 重算 64-bit Hash |
| `Validate(out errors)` | 检查：空路径、重复、过期 Hash、Hash 碰撞、Hash=0、空段、缺少父路径 |
| `NormalizePath(path)` | `/` → `_` 统一分隔符 |

---

## 9. 代码生成

### 9.1 生成文件

运行 `RedDotPathEditor` →「生成常量」/「生成注册」→ 输出两个文件：

**RedDotPaths.cs** — 编译期常量（`const long`）：

```csharp
namespace RedDot
{
    public static class RedDotPaths
    {
        /// <summary>根节点</summary>
        public const long Root = unchecked((long)0x9DC5812B6A3F7E41UL);

        /// <summary>背包</summary>
        public const long Root_Bag = unchecked((long)0xC4E8A120F73D9B56UL);

        /// <summary>物品数量</summary>
        public const long Root_Bag_ItemCount = unchecked((long)0x2B7F3C9D14E8A605UL);

        // ...
    }
}
```

**RedDotPathRegistration.cs** — 启动注册：

```csharp
public static class RedDotPathsRegistration
{
    public static void RegisterAll(RedDotManager mgr)
    {
        mgr.RegisterNode(RedDotPaths.Root,              0L,                    true);
        mgr.RegisterNode(RedDotPaths.Root_Bag,          RedDotPaths.Root,      true);
        mgr.RegisterNode(RedDotPaths.Root_Bag_ItemCount, RedDotPaths.Root_Bag, true);
        // ... 按深度优先顺序注册全部路径
    }
}
```

### 9.2 注册时机

```csharp
// RedDotManager 首次被访问时自动触发，无需业务代码手动调用
public void EnsureRegistered()
{
    if (_registered || _isRegistering) return;
    _isRegistering = true;
    try   { RedDotPathsRegistration.RegisterAll(this); _registered = true; }
    finally { _isRegistering = false; }
}
```

---

## 10. UI 绑定 — RedDotBinder

**文件**：`UI/RedDotBinder.cs`  
**用法**：`[AddComponentMenu("RedDot/RedDot Binder")]`

### 10.1 Inspector 配置

```
RedDotBinder
├── Red Dot Path Hash    [RedDotPathSelector] ▼   ← 下拉选路径（long 序列化）
├── Max Display Number   99
└── 子节点（按命名查找）:
    ├── New           (GameObject)   ← IsNew 类型图标
    ├── Normal        (GameObject)   ← Normal 类型图标
    ├── CanUpgrade    (GameObject)   ← CanUpdate 类型图标
    ├── Tips          (GameObject)   ← Tips 类型图标
    ├── Num           (GameObject)   ← Number 类型容器
    │   └── NumCount  (TextMeshProUGUI) ← 数字文本
```

### 10.2 工作流程

```
RedDotManager 通知
    ↓
OnRedDotChanged(RedDotState state)
    ↓
Refresh(state)
    ↓
  1. 无变化（visible + count + type 全同）→ 跳过
  2. 类型变了 → 隐藏旧类型 GameObject
  3. 更新缓存（_lastVisible / _lastCount / _lastType）
  4. 显示新类型 GameObject
  5. Number 类型 → 更新 TMP 文本（超 maxDisplayNumber 则显示 "99+"）
```

### 10.3 运行时 API

```csharp
binder.ForceRefresh();                              // 强制刷新
binder.SetPathHash(RedDotPaths.Root_Mail_System);   // 运行时切换路径
```

---

## 11. 编辑器工具

### 11.1 路径编辑器 (RedDotPathEditor)

**入口**：`Tools → RedDot → 红点路径编辑器`

**功能**：
- 双栏界面：左侧树浏览 + 右侧详情/添加
- 搜索过滤（自动展开）
- 展开/收起全部、排序
- 快速添加：选择父路径 → 输入名称（支持多行批量）
- 批量导入：粘贴路径列表（每行一个）
- 重命名、删除（含确认对话框）、复制路径/Hash/C# 常量名
- 右键上下文菜单
- 代码生成：重算 Hash、校验、生成常量/注册文件、一键生成全部
- 支持 Undo（Ctrl+Z）
- 脏标记提示（黄色圆点）

**数据流**：

```
RedDotPathDefinition.asset (ScriptableObject)
        ↑ 读取 / 写入 ↓
  RedDotPathEditor (EditorWindow)
        ↓ 生成
  RedDotPaths.cs + RedDotPathRegistration.cs
        ↓ 编译
  运行时使用
```

### 11.2 RedDotPathSelector 下拉

**用法**：

```csharp
[RedDotPathSelector]
[SerializeField] private long _redDotPathHash;  // 序列化为 long
```

**行为**：
- 反射扫描所有程序集找到 `RedDot.RedDotPaths` 类型（`const long` 字段）
- 字段名按 `_` 分组构建树形 `AdvancedDropdown`
- 叶子项显示完整路径便于搜索（如 `Adv   (Root_Shop_Lottery_Adv)`）
- 选中的 hash 写入 `SerializedProperty.longValue`

### 11.3 运行时监视器 (RedDotMonitor)

**入口**：`Tools → RedDot → Monitor`

- 仅 Play Mode 有效（否则显示 "Enter Play Mode"）
- 实时显示所有节点的 Self、Total、Type、Listener 计数
- 0.3s 自动刷新
- 支持搜索过滤
- 通过反射访问 `RedDotManager._data` 私有字段读取数据

---

## 12. 测试与调试

### 12.1 手动测试面板 (TestPanel)

**文件**：`Test/TestPanel.cs`

| 功能 | 路径 | 类型 | 操作 |
|------|------|------|------|
| 背包物品 | `Root_Bag_ItemCount` | Number | 增加/减少计数 |
| 属性点 | `Root_Role_AttrPoint` | CanUpdate | 增加/减少 |
| 免费抽奖 | `Root_Shop_Lottery_Free` | Tips | 存在/移除 |
| 广告商店 | `Root_Shop_Lottery_Adv` | Tips | 存在/移除 |
| 系统邮件 | `Root_Mail_System` | IsNew | 增加/减少 |
| 个人邮件 | `Root_Mail_Person` | IsNew | 增加/减少 |

### 12.2 Debug.Log 验证

```csharp
// 每个操作后输出自身红点和父节点聚合值
Debug.Log($"[红点测试] 系统邮件: {_systemMailCount}  |  邮箱红点: {
    RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Mail)}");
```

观察 Console：
- 添加系统邮件 2 封 + 个人邮件 3 封 → 邮箱红点 = 5
- 清除系统邮件 → 邮箱红点 = 3

### 12.3 监视器窗口

Play Mode 下打开 `Tools → RedDot → Monitor`，实时观察整棵红点树的状态变化。

---

## 13. 完整 API 参考

### RedDotManager

```csharp
// 单例
static RedDotManager Instance { get; }
static bool HasInstance { get; }

// 注册
void EnsureRegistered();
int  RegisterNode(long pathHash, long parentPathHash, bool isStatic = false);

// 静态路径写入
void SetRedDot(long pathHash, int count, RedDotType type = RedDotType.Normal);
void ClearNode(long pathHash);                          // 清零自身（不递归）
void ClearNodeRecursive(long pathHash);                 // 递归清整棵子树
bool RemoveDynamicLeafNode(long pathHash);

// 查询
int              GetRedDot(long pathHash);              // TotalCount
int              GetSelfRedDot(long pathHash);          // SelfCount
RedDotState      GetState(long pathHash);               // 完整快照
RedDotType       GetEffectiveType(long pathHash);       // 最高优先级类型
List<RedDotType> GetActiveTypes(long pathHash);         // 所有活跃类型（分配版）
void             GetActiveTypes(long pathHash, List<RedDotType> outList); // 零分配版

// 监听
void AddListener(long pathHash, Action<RedDotState> callback);
void RemoveListener(long pathHash, Action<RedDotState> callback);

// 管理
void ResetAll();

// 动态节点（parentHash + childId，零碰撞路由）
int         RegisterDynamicNode(long parentHash, int childId);
void        SetRedDot(long parentHash, int childId, int count, RedDotType type = RedDotType.Normal);
int         GetRedDot(long parentHash, int childId);
int         GetSelfRedDot(long parentHash, int childId);
RedDotState GetState(long parentHash, int childId);
void        ClearNode(long parentHash, int childId);    // 递归清子树
void        AddListener(long parentHash, int childId, Action<RedDotState> callback);
void        RemoveListener(long parentHash, int childId, Action<RedDotState> callback);
```

### RedDotTrie

```csharp
int  RegisterNode(long pathHash, long parentHash);
int  FindIndex(long pathHash);
bool TryFindIndex(long pathHash, out int idx);
int  GetParentIndex(int idx);
long GetPathHash(int idx);
bool ContainsPath(long pathHash);
int  GetChildCount(int idx);
void GetChildren(int idx, List<int> outChildren);
bool TryRemoveNode(long pathHash);                      // 删除叶子，回收槽位
void Clear();
```

### RedDotDataStore

```csharp
void       EnsureCapacity(int idx);
int        SetSelfCount(int nodeIndex, int count);
int        GetSelfCount(int nodeIndex);
int        GetTotalCount(int nodeIndex);
bool       AddDeltaToTotal(int nodeIndex, int delta);
bool       SetSelfTypeFlag(int nodeIndex, RedDotType type);
RedDotType GetSelfTypeFlag(int nodeIndex);
bool       SetTypeFlags(int nodeIndex, RedDotType flags);
RedDotType GetTypeFlags(int nodeIndex);
RedDotType GetHighestType(int nodeIndex);
void       GetActiveTypes(int nodeIndex, List<RedDotType> outList);
RedDotState GetState(int nodeIndex, long pathHash);
void       AddListener(int nodeIndex, Action<RedDotState> cb);
void       RemoveListener(int nodeIndex, Action<RedDotState> cb);
void       NotifyListeners(int nodeIndex, RedDotState state);
bool       HasListeners(int nodeIndex);
int        ListenerCount(int nodeIndex);
void       ResetNode(int nodeIndex);
void       Clear();
```

### RedDotHash

```csharp
static long Compute(string value);                             // FNV-1a 64-bit
static long Compute(byte[] data);
static long ComputeDynamic(long parentHash, int childId);     // 动态节点，零分配
```

### RedDotState

```csharp
readonly long       PathHash;
readonly int        SelfCount;
readonly int        TotalCount;
readonly RedDotType EffectiveType;
bool Visible { get; }  // TotalCount > 0
```

### RedDotBinder

```csharp
void ForceRefresh();
void SetPathHash(long newPathHash);
```

---

## 14. 最佳实践

### 14.1 添加新红点路径

1. 打开 `Tools → RedDot → 红点路径编辑器`
2. 在「添加新路径」中选择父路径（或根节点）
3. 输入名称（如 `Bag_NewItem`），点击「添加」
4. 点击「一键生成全部」
5. 代码中使用 `RedDotPaths.Root_Bag_NewItem`

### 14.2 业务代码调用

```csharp
var mgr = RedDotManager.Instance;

// 静态路径（编辑器预定义）
mgr.SetRedDot(RedDotPaths.Root_Mail_System, 3, RedDotType.IsNew);
mgr.ClearNode(RedDotPaths.Root_Mail_System);
bool hasMail = mgr.GetRedDot(RedDotPaths.Root_Mail) > 0;

// 动态节点（运行时创建，如邮件第 1001 封）
mgr.RegisterDynamicNode(RedDotPaths.Root_Mail, 1001);
mgr.SetRedDot(RedDotPaths.Root_Mail, 1001, 1, RedDotType.IsNew);
mgr.ClearNode(RedDotPaths.Root_Mail, 1001);       // 递归清并移除动态节点
int count = mgr.GetRedDot(RedDotPaths.Root_Mail, 1001);

// 监听
mgr.AddListener(RedDotPaths.Root_Mail, state => {
    mailIcon.SetActive(state.Visible);
});
mgr.AddListener(RedDotPaths.Root_Mail, 1001, state => {
    Debug.Log($"邮件 1001 红点: {state.TotalCount}");
});
```

### 14.3 UI 挂载 RedDotBinder

1. 创建 UI GameObject（如邮件按钮的子节点）
2. 添加 `RedDotBinder` 组件
3. 在 Inspector 中选择 `Red Dot Path Hash`（下拉搜索）
4. 按命名约定创建子节点：`New`、`Normal`、`CanUpgrade`、`Tips`、`Num/NumCount`
5. 运行，红点自动显示/隐藏

### 14.4 性能注意事项

- **SetRedDot 是 O(depth)**，不是 O(n)；深度 5 以内的树，单次调用 < 1μs
- **不要在 Update 中频繁调用 SetRedDot**；红点数据变化通常是事件驱动的
- **AddListener 注册时立即回调**当前状态，UI 可以在 OnEnable 中订阅而不需要手动查询
- **TotalCount 是增量维护的**，不要试图自己累加子节点
- **GetActiveTypes 有零分配重载**，传复用 List 避免 GC
- **动态节点通过复合键路由**，不受 64-bit hash 碰撞影响

### 14.5 常见问题

| 问题 | 原因 | 解决 |
|------|------|------|
| 红点不显示 | 路径未注册 | `EnsureRegistered()` 已自动注册，检查路径是否在 PathDefinition 中 |
| 红点不消失 | count=0 但 type 残留 | `ClearNode()` 或 `SetRedDot(hash, 0)` |
| 退出 Play Mode 报错 | OnDestroy 触发懒创建 | 用 `HasInstance` 判断后再访问 |
| 父节点红点数不对 | 子节点 count 设为负 | count 自动 clamp 到 0 |
| Inspector 无路径下拉 | 字段类型不是 `long` | 确认字段为 `private long _redDotPathHash` |
