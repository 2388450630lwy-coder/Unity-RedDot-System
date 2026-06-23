# RedDot — Unity 高性能红点系统

> **版本** 2.0 · **Unity** 2022.3+ · **C#** .NET Standard 2.1 · **依赖** 零第三方包

树形增量聚合 · 编辑期 Hash 常量 · 运行时零字符串分配 · O(depth) 写入 · O(1) 查询

---

## 目录

1. [工程背景与痛点](#1-工程背景与痛点)
2. [设计目标与技术选型](#2-设计目标与技术选型)
3. [系统架构](#3-系统架构)
4. [核心数据结构](#4-核心数据结构)
5. [关键算法与流程](#5-关键算法与流程)
6. [路径标识与哈希](#6-路径标识与哈希)
7. [动态节点机制](#7-动态节点机制)
8. [类型系统](#8-类型系统)
9. [代码生成与注册](#9-代码生成与注册)
10. [UI 绑定 — RedDotBinder](#10-ui-绑定--reddotbinder)
11. [编辑器工具](#11-编辑器工具)
12. [API 参考](#12-api-参考)
13. [性能与内存](#13-性能与内存)
14. [接入指南](#14-接入指南)

---

## 1. 工程背景与痛点

红点系统是 UI 状态管理的子问题：**一棵有向树，叶子节点承载业务状态，祖先节点承载聚合状态**。常见工程陷阱：

| 问题域 | 根因 | 后果 |
|--------|------|------|
| **聚合一致性** | 子节点变化后未向上传播 | 父子红点不同步，需人工对账 |
| **路径标识** | 运行时使用 `string` 路径 | 拼写错误、GC 分配、重构成本高 |
| **查询复杂度** | 每次读祖先都遍历子树 | 节点增多后读操作退化 |
| **动态实体** | 列表项 ID 不固定，无统一注册模型 | 路径拼接散落、泄漏未清理节点 |
| **类型表达** | 布尔显隐无法区分样式 | UI 需额外分支判断 |
| **可观测性** | 无运行时树状态视图 | 调试依赖 Log，定位成本高 |

RedDot 将上述问题收敛为：**Trie 路由 + 平行数组存储 + 增量 delta 传播 + 编辑期代码生成**。

---

## 2. 设计目标与技术选型

| 目标 | 实现策略 |
|------|----------|
| 写入 O(depth) | `SetRedDot` 仅沿父链传播 delta，不扫描子树 |
| 查询 O(1) | `TotalCount` 预聚合，`GetRedDot` 直接读数组 |
| 零运行时字符串 | 路径 → FNV-1a 64-bit `long`，编辑期生成 `const` |
| 动态列表支持 | `(parentHash, childId)` 复合键旁路 hash 碰撞 |
| 低 GC | `RedDotState` 为 `readonly struct`；监听器传快照 |
| 可测试性 | `RedDotDataStore` / `RedDotTrie` 不依赖 Unity API |

### 与常见方案对比

| 维度 | 手写冒泡 | 全局事件总线 | RedDot |
|------|---------|-------------|--------|
| 聚合维护 | 业务手动 | 业务手动 | `TotalCount` 增量自动 |
| 路径查找 | O(1)~O(n) | — | Trie `Dictionary` O(1) |
| 写入传播 | 不定 | 广播所有订阅者 | 仅变化节点 + 祖先链 |
| 动态节点 | 自实现 | 自实现 | 内置复合键 + 懒注册 |
| 运行时分配 | 视实现 | 常有 | 路径操作为 `long`，无 string |

---

## 3. 系统架构

```
┌─────────────────────────────────────────────────────────────────┐
│  Gameplay                                                        │
│  SetRedDot(pathHash, count, type)                               │
│  SetRedDot(parentHash, childId, count, type)                    │
│  AddListener(pathHash | parentHash+childId, Action<RedDotState>)│
└───────────────────────────┬─────────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────────┐
│  RedDotManager (Facade)                                          │
│  · MonoBehaviour 单例，DontDestroyOnLoad                          │
│  · SetRedDotByIndex → delta 传播 → RefreshNodeFlags → Notify    │
│  · _dynamicKeyToIndex: (long, long) → nodeIndex                 │
│  · _staticPathHashes: 静态节点删除保护                             │
└──────────────┬────────────────────────────┬─────────────────────┘
               │                            │
┌──────────────▼──────────────┐ ┌───────────▼────────────────────┐
│  RedDotTrie (Routing)        │ │  RedDotDataStore (Data)         │
│  List<TrieNode>             │ │  int[] _selfCounts              │
│  Dictionary<long,int>       │ │  int[] _totalCounts             │
│  pathHash → nodeIndex       │ │  RedDotType[] _selfTypeFlags    │
│  ParentIndex 父链导航        │ │  RedDotType[] _totalTypeFlags   │
└─────────────────────────────┘ │  List<Action<RS>>[] _listeners  │
                                └────────────────────────────────┘
┌─────────────────────────────────────────────────────────────────┐
│  RedDotHash · RedDotState · RedDotType · RedDotPathDefinition   │
└─────────────────────────────────────────────────────────────────┘
```

**模块职责分离**：

- `RedDotTrie`：只管拓扑（父子关系、hash 查找），不存计数
- `RedDotDataStore`：只管数值与监听器，不知路径字符串
- `RedDotManager`：编排写入流程、类型聚合、动态节点生命周期

---

## 4. 核心数据结构

### 4.1 RedDotTrie — 路由层

```csharp
struct TrieNode {
    int                   ParentIndex;   // -1 = Root
    long                  PathHash;
    Dictionary<long, int> Children;      // 懒初始化，null = 无子节点
}

List<TrieNode>        _nodes;           // [0] = Root (PathHash=0)
Dictionary<long, int> _pathToIndex;     // pathHash → nodeIndex
```

```
_pathToIndex: { 0x625036826C39B485 → 2, 0x9965F770C376C68F → 5, ... }

_nodes:
  [0] Root                Parent=-1  Children={...}
  [1] Root_Bag            Parent=0
  [2] Root_Mail           Parent=0   Children={...→5, ...→6}
  [5] Root_Mail_System    Parent=2   Children=null (leaf)
  [6] Root_Mail_Person    Parent=2   Children=null (leaf)
```

| 操作 | 复杂度 | 说明 |
|------|--------|------|
| `RegisterNode(pathHash, parentHash)` | O(1) | 幂等，已存在直接返回 index |
| `FindIndex(pathHash)` | O(1) | Dictionary 查找 |
| `GetParentIndex(idx)` | O(1) | 数组随机访问 |
| `GetChildren(idx, buffer)` | O(k) | k = 子节点数，写入调用方 buffer |
| `TryRemoveNode(pathHash)` | O(1) | 仅叶子且非 Root；从父 Children 移除 |

### 4.2 RedDotDataStore — 数据层

所有字段按 `nodeIndex` **平行数组**索引，缓存友好，避免嵌套字典：

```
_capacity = 1024 → 2048 → 4096 ...（×2 扩容）

_selfCounts[nodeIndex]      int          自身计数（业务 SetRedDot 写入）
_totalCounts[nodeIndex]     int          聚合计数（自身 + 子孙 SelfCount 之和）
_selfTypeFlags[nodeIndex]   RedDotType   自身类型（count=0 时清零）
_totalTypeFlags[nodeIndex]  RedDotType   聚合类型（self | children OR）
_listeners[nodeIndex]       List<Action<RedDotState>>  监听器（空时置 null）
```

| 字段 | 叶子节点 | 容器节点 |
|------|---------|---------|
| `SelfCount` | 业务直接设置的值 | 业务直接设置的值（不含子孙） |
| `TotalCount` | = SelfCount | SelfCount + Σ子孙 SelfCount |
| `Visible` | TotalCount > 0 | TotalCount > 0 |

### 4.3 RedDotState — 监听器快照

```csharp
public readonly struct RedDotState
{
    public readonly long       PathHash;
    public readonly int        SelfCount;
    public readonly int        TotalCount;
    public readonly RedDotType EffectiveType;
    public bool Visible => TotalCount > 0;
}
```

栈分配 struct，回调收到后无需二次查询 Manager。

---

## 5. 关键算法与流程

### 5.1 SetRedDot 写入流程

```csharp
private void SetRedDotByIndex(int nodeIndex, int count, RedDotType type)
{
    // 1. 写入叶子/目标节点
    int delta = _data.SetSelfCount(nodeIndex, count);   // 返回 count - oldSelf
    _data.SetSelfTypeFlag(nodeIndex, count > 0 ? type : 0);
    RefreshNodeFlags(nodeIndex);                         // self | children → flags
    if (changed) NotifyNode(nodeIndex);

    // 2. 沿父链传播 delta
    int current = _trie.GetParentIndex(nodeIndex);
    while (current != INVALID_INDEX)
    {
        _data.AddDeltaToTotal(current, delta);           // 祖先 TotalCount += delta
        RefreshNodeFlags(current);
        if (changed) NotifyNode(current);
        current = _trie.GetParentIndex(current);
    }
}
```

**关键不变量**：任意节点 `TotalCount` = 自身 `SelfCount` + 所有子孙 `SelfCount` 之和。`SetSelfCount` 在目标节点同时更新其 `SelfCount` 和 `TotalCount`（叶子两者相等），祖先仅通过 `AddDeltaToTotal` 接收 delta。

```
Root                     Self=0  Total=8
├── Mail                 Self=0  Total=8     ← delta=+3 沿链传播
│   ├── System  Self=3   Total=3            ← SetRedDot 目标
│   └── Person  Self=5   Total=5
└── Shop ...
```

**复杂度**：O(depth)，与树总节点数 N 无关。

### 5.2 RefreshNodeFlags — 类型聚合

```csharp
RedDotType flags = _data.GetSelfTypeFlag(nodeIndex);
_trie.GetChildren(nodeIndex, _childrenBuffer);
for (int i = 0; i < _childrenBuffer.Count; i++)
    flags |= _data.GetTypeFlags(_childrenBuffer[i]);   // 读子节点聚合类型
_data.SetTypeFlags(nodeIndex, flags);
```

父节点类型 = 自身类型 **位或** 所有子节点聚合类型。`GetEffectiveType` 按优先级表取最高优先级单项。

### 5.3 监听器通知

```csharp
// 倒序遍历，安全处理回调中自注销
for (int i = list.Count - 1; i >= 0; i--)
    list[i]?.Invoke(state);
```

`AddListener` 注册时**立即回调一次**当前状态，UI 在 `OnEnable` 订阅即可，无需额外查询。

---

## 6. 路径标识与哈希

运行时标识符为 **FNV-1a 64-bit** `long`，非字符串：

```
编辑期:  "Root_Mail_System"
           ↓ RedDotHash.Compute()
           ↓ 写入 RedDotPaths.cs
运行期:  RedDotPaths.Root_Mail_System  (= 0x9965F770C376C68F)
```

```csharp
public static long Compute(string value)
{
    ulong hash = FNV64_OFFSET_BASIS;  // 14695981039346656037
    for (int i = 0; i < value.Length; i++)
    {
        char c = value[i];
        hash ^= (byte)(c & 0xFF);        hash *= FNV64_PRIME;  // 1099511628211
        hash ^= (byte)((c >> 8) & 0xFF); hash *= FNV64_PRIME;
    }
    return unchecked((long)hash);
}
```

- 空字符串 → `0`（Root 专用）
- UTF-16 按小端两 byte 逐字节哈希
- 64-bit 空间：10⁹ 条路径碰撞概率约 0.001%，静态路径可在编辑器 `Validate` 中检测碰撞

---

## 7. 动态节点机制

### 7.1 使用场景

运行时创建的实体：邮件 ID、背包格子、活动期数。数量不定，无法在编辑期预生成全部路径。

### 7.2 双重路由

```
                    ┌─ _dynamicKeyToIndex[(parentHash, childId)] → nodeIndex  (主路由)
RegisterDynamicNode ┤
                    └─ pathHash = ComputeDynamic(parent, childId) → Trie 注册  (辅路由)
```

**复合键旁路设计**：即使 `ComputeDynamic` 产生 hash 碰撞，`(parentHash, childId)` 仍能精确路由，动态节点不受碰撞影响。

### 7.3 childId 类型

| 重载 | Hash 输入 | 适用 |
|------|----------|------|
| `ComputeDynamic(parent, int childId)` | parent 8B + childId 4B | 常规整数 ID |
| `ComputeDynamic(parent, long childId)` | parent 8B + childId 8B | 雪花 ID 等超 int 范围 |

内部复合键统一为 `(long parentHash, long childId)`，`int` API 隐式提升为 `long`。

### 7.4 懒注册

```csharp
public void SetRedDot(long parentHash, long childId, int count, RedDotType type)
{
    int nodeIndex = FindDynamicIndex(parentHash, childId);
    if (nodeIndex == INVALID_INDEX)
        nodeIndex = RegisterDynamicNode(parentHash, childId);  // 自动注册
    SetRedDotByIndex(nodeIndex, count, type);
}
```

### 7.5 生命周期

| 操作 | 静态节点 | 动态节点 |
|------|---------|---------|
| 注册 | 编辑期 `RegisterAll` | `RegisterDynamicNode` 或 `SetRedDot` 懒注册 |
| 清零 | `ClearNode` / `SetRedDot(0)` | 同上 |
| 递归清理 | `ClearNodeRecursive` → SelfCount=0 | `ClearNodeRecursive` → `RemoveDynamicLeafNode` |
| 物理删除 | 不可删（`_staticPathHashes` 保护） | 叶子 + 无监听器 + 无子节点时可删 |

---

## 8. 类型系统

```csharp
public enum RedDotType
{
    Normal    = 1 << 0,
    Tips      = 1 << 1,
    CanUpdate = 1 << 2,
    IsNew     = 1 << 3,
    Number    = 1 << 4,
}
```

**优先级**（`RedDotTypeHelper.PriorityOrder`）：

```
IsNew > CanUpdate > Tips > Normal > Number
```

```
Hero (CanUpdate | IsNew)       ← 父节点 flags = self | Σchildren
├── Hero_New      (IsNew)
└── Hero_Upgrade  (CanUpdate)
```

- `GetEffectiveType(hash)` → 最高优先级单项（UI 显示用）
- `GetActiveTypes(hash, outList)` → 所有活跃类型（零分配重载）

---

## 9. 代码生成与注册

### 9.1 数据流

```
RedDotPathDefinition.asset (ScriptableObject)
        ↕
RedDotPathEditor (Tools → RedDot → 红点路径编辑器)
        ↓ 生成
RedDotPaths.cs              — public const long 常量
RedDotPathRegistration.cs   — RegisterAll(mgr) 深度优先注册
        ↓ 编译
运行时 EnsureRegistered() 自动调用 RegisterAll
```

### 9.2 生成示例

```csharp
// RedDotPaths.cs
public static class RedDotPaths
{
    public const long Root_Mail_System = unchecked((long)0x9965F770C376C68FUL);
}

// RedDotPathRegistration.cs
public static void RegisterAll(RedDotManager mgr)
{
    mgr.RegisterNode(RedDotPaths.Root,              0L,                    true);
    mgr.RegisterNode(RedDotPaths.Root_Mail,         RedDotPaths.Root,      true);
    mgr.RegisterNode(RedDotPaths.Root_Mail_System,  RedDotPaths.Root_Mail, true);
}
```

### 9.3 Validate 检查项

空路径、重复路径、Hash 过期、Hash 碰撞、Hash=0、空段、缺少父路径。

---

## 10. UI 绑定 — RedDotBinder

**文件**：`UI/RedDotBinder.cs`

### 10.1 子节点约定

```
RedDotBinder
├── New           → RedDotType.IsNew
├── Normal        → RedDotType.Normal
├── CanUpgrade    → RedDotType.CanUpdate
├── Tips          → RedDotType.Tips
├── Num           → RedDotType.Number
│   └── NumCount  → TextMeshProUGUI（超 maxDisplayNumber 显示 "99+"）
```

### 10.2 刷新逻辑

```
OnRedDotChanged(state)
  → visible/count/type 三元组未变 → 跳过（避免无效 SetActive）
  → type 变化 → 隐藏旧类型 GameObject
  → 显示新类型 GameObject / 更新 TMP 文本
```

### 10.3 绑定模式

| 模式 | API | 监听 |
|------|-----|------|
| 静态 | Inspector 选路径 / `SetPathHash(hash)` | `AddListener(hash, cb)` |
| 动态 | `SetDynamicNode(parentHash, childId)` | `AddListener(parent, childId, cb)` |

`OnEnable` 订阅，`OnDisable` 退订；`SetPathHash` / `SetDynamicNode` 运行时切换绑定目标。

---

## 11. 编辑器工具

| 工具 | 入口 | 技术要点 |
|------|------|----------|
| **路径编辑器** | `Tools → RedDot → 红点路径编辑器` | SO 读写、Undo、批量导入、Hash 重算与校验 |
| **路径选择器** | `[RedDotPathSelector]` on `long` 字段 | 反射扫描 `RedDotPaths` const 字段，AdvancedDropdown 树 |
| **运行时监视器** | `Tools → RedDot → Monitor` | Play Mode only；反射读 `_data`；0.3s 刷新 |

---

## 12. API 参考

### RedDotManager

```csharp
// 单例
static RedDotManager Instance { get; }
static bool HasInstance { get; }

// 注册
void EnsureRegistered();
int  RegisterNode(long pathHash, long parentPathHash, bool isStatic = false);

// ── 静态路径 ──
void SetRedDot(long pathHash, int count, RedDotType type = RedDotType.Normal);
void ClearNode(long pathHash);
void ClearNodeRecursive(long pathHash);
bool RemoveDynamicLeafNode(long pathHash);

int              GetRedDot(long pathHash);
int              GetSelfRedDot(long pathHash);
RedDotState      GetState(long pathHash);
RedDotType       GetEffectiveType(long pathHash);
void             GetActiveTypes(long pathHash, List<RedDotType> outList);

void AddListener(long pathHash, Action<RedDotState> callback);
void RemoveListener(long pathHash, Action<RedDotState> callback);

// ── 动态节点 ──
int  RegisterDynamicNode(long parentHash, int childId);
int  RegisterDynamicNode(long parentHash, long childId);

void SetRedDot(long parentHash, int/long childId, int count, RedDotType type = Normal);  // 未注册自动注册
int         GetRedDot(long parentHash, int/long childId);
int         GetSelfRedDot(long parentHash, int/long childId);
RedDotState GetState(long parentHash, int/long childId);
void        ClearNode(long parentHash, int/long childId);
void        AddListener(long parentHash, int/long childId, Action<RedDotState> callback);
void        RemoveListener(long parentHash, int/long childId, Action<RedDotState> callback);

void ResetAll();
```

### RedDotHash

```csharp
static long Compute(string value);
static long Compute(byte[] data);
static long ComputeDynamic(long parentHash, int childId);   // 4B childId
static long ComputeDynamic(long parentHash, long childId);  // 8B childId
```

### RedDotBinder

```csharp
void SetPathHash(long newPathHash);
void SetDynamicNode(long parentHash, int childId);
void SetDynamicNode(long parentHash, long childId);
void ForceRefresh();
```

---

## 13. 性能与内存

| 指标 | 值 |
|------|-----|
| `SetRedDot` | O(depth)，典型 depth ≤ 5，< 1μs |
| `GetRedDot` / `GetState` | O(1) |
| `RefreshNodeFlags` | O(k)，k = 直接子节点数 |
| 路径操作 GC | 0（`long` hash，无 string） |
| 初始容量 | Trie / DataStore 默认 1024 槽，×2 扩容 |
| 监听器 | 每节点 `List<Action<>>`，无监听时 null |

**注意事项**：

- 不要在 `Update` 中轮询 `SetRedDot`；红点变化应事件驱动
- `OnDestroy` 中用 `HasInstance` 守卫，避免退出 Play Mode 触发懒创建
- `GetActiveTypes(hash, outList)` 传入复用 List，避免分配版 `new List<>`
- 动态节点 `AddListener` 前需已注册（`SetRedDot` 可触发懒注册）

---

## 14. 接入指南

### 14.1 添加静态路径

1. `Tools → RedDot → 红点路径编辑器` → 添加路径
2. 「一键生成全部」
3. 业务：`mgr.SetRedDot(RedDotPaths.Root_XXX, count, type)`

### 14.2 动态列表

```csharp
// SetRedDot 自动注册，无需手动 RegisterDynamicNode
mgr.SetRedDot(RedDotPaths.Root_Mail, mailId, 1, RedDotType.IsNew);
mgr.SetRedDot(RedDotPaths.Root_Mail, snowflakeId, 1, RedDotType.IsNew);

// 列表项 UI
binder.SetDynamicNode(RedDotPaths.Root_Mail, mailId);
```

### 14.3 项目结构

```
Assets/Scripts/RedDot/
├── Core/       RedDotManager · RedDotTrie · RedDotDataStore · RedDotHash
├── Generated/  RedDotPaths.cs · RedDotPathRegistration.cs
├── UI/         RedDotBinder.cs
├── Editor/     RedDotPathEditor · RedDotMonitor
└── Test/       TestPanel.cs
```

---

## License

MIT
