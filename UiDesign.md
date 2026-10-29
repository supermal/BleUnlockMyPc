# UiDesign.md — 通用界面设计规范（Web / WPF）

> 版本 1.0 · 参考来源：Windows 11 25H2「设置」应用（Fluent 2 卡片列表）与 GitLab 新版界面（岛屿式布局）
> 适用：Web（Blazor / 任意前端，基于 CSS 变量）与桌面（WPF，可配合 WPF UI 使用）

本文档定义一套平台无关的设计语言：先给出原则和设计令牌（Design Tokens），再定义外壳布局和组件规格，最后给出 Web 与 WPF 两端的落地映射。所有数值以 **px（Web）= DIP（WPF）** 为单位，两端可直接对应。

---

## 1. 设计原则

1. **岛屿承载工作，海面承载导航。** 应用外壳（顶栏、侧边导航、工具条）放在底色层（海面），当前正在处理的内容放在一块圆角面板（岛）上。视觉重心永远落在岛上。
2. **靠明度分层，少用线条。** 层级通过背景明度差体现：海面 < 岛 < 卡片 < 控件。边框只用极淡的描边辅助，不用粗分隔线划分区域。
3. **组内紧、组间松。** 同一组的卡片间距 4px，不同区块之间 24–48px。间距本身就是分组信息。
4. **色彩克制。** 界面主体是中性灰阶；强调色只出现在选中标识、焦点、链接和少数主操作上。状态色只用于表达状态。
5. **内容限宽。** 内容列有最大宽度，宽屏下不无限拉伸，保证阅读与扫视舒适。
6. **两种密度，按场景选择。** 「舒适」用于设置、详情、表单（Windows 设置风格）；「紧凑」用于列表、工作台、数据密集页面（GitLab 风格）。同一页面只用一种。
7. **键盘优先，可访问。** 全局搜索有快捷键，所有交互元素有清晰焦点环，状态不只依赖颜色。

---

## 2. 设计令牌

令牌命名采用 `类别.用途.变体`。Web 端转为 CSS 变量 `--ui-类别-用途-变体`，WPF 端转为资源键 `Ui.类别.用途.变体`（见第 7、8 节）。

### 2.1 颜色 — 表面层级

| 令牌 | 用途 | 浅色 | 深色 |
|---|---|---|---|
| `bg.canvas` | 海面：窗口底色，顶栏、侧栏、工具条所在层 | `#F3F3F3` | `#1C1C1C` |
| `bg.island` | 岛：主内容面板 | `#FFFFFF` | `#242424` |
| `bg.card` | 岛内卡片、设置项 | `#FFFFFF` | `#2B2B2B` |
| `bg.layer` | 岛内的次级色带（过滤栏、卡片展开区、表头） | `#F7F7F8` | `#202020` |
| `bg.control` | 按钮、输入框默认背景 | `#FFFFFF` | `#333333` |
| `bg.control.hover` | 控件悬停 | `#F5F5F5` | `#3A3A3A` |
| `bg.control.pressed` | 控件按下 | `#EDEDED` | `#2F2F2F` |
| `bg.subtle.hover` | 无背景元素（导航项、列表行、图标按钮）悬停 | `#0000000A` | `#FFFFFF0F` |
| `bg.subtle.selected` | 导航选中、列表选中 | `#0000000F` | `#FFFFFF14` |
| `bg.badge` | 计数徽章、令牌块、状态胶囊底色 | `#ECECEE` | `#3A3A3A` |
| `bg.overlay` | 模态遮罩 | `#00000066` | `#0000008C` |
| `bg.flyout` | 下拉、菜单、弹出层 | `#FFFFFF` | `#2C2C2C` |

> 说明：带 8 位十六进制的值为 `#RRGGBBAA`（Web）。WPF 中写作 `#AARRGGBB`，例如 `#0000000A` → `#0A000000`。第 8 节的 WPF 资源已按 WPF 格式给出。

### 2.2 颜色 — 描边

| 令牌 | 用途 | 浅色 | 深色 |
|---|---|---|---|
| `stroke.island` | 岛的外描边 | `#E5E5E5` | `#FFFFFF0F` |
| `stroke.card` | 卡片描边 | `#E5E5E5` | `#FFFFFF12` |
| `stroke.divider` | 卡片内部、页签基线等分隔线 | `#EBEBEB` | `#FFFFFF14` |
| `stroke.control` | 按钮、输入框描边 | `#D6D6D6` | `#FFFFFF1F` |
| `stroke.control.strong` | 输入框聚焦前的下边线 / 强调描边 | `#8A8A8A` | `#9A9A9A` |
| `stroke.focus.outer` | 焦点环外圈 | `#1A1A1A` | `#FFFFFF` |
| `stroke.focus.inner` | 焦点环内圈 | `#FFFFFF` | `#000000` |

### 2.3 颜色 — 文字与图标

| 令牌 | 用途 | 浅色 | 深色 |
|---|---|---|---|
| `text.primary` | 标题、正文、标签 | `#1A1A1A` | `#F2F2F2` |
| `text.secondary` | 说明、元信息、键值对中的值 | `#5F5F66` | `#B8B8BE` |
| `text.tertiary` | 占位符、计数数字、最弱提示 | `#8B8B93` | `#8E8E96` |
| `text.disabled` | 禁用 | `#A8A8AE` | `#6B6B72` |
| `text.onAccent` | 强调色背景上的文字 | `#FFFFFF` | `#000000` |
| `text.onNeutral` | 中性主按钮上的文字 | `#FFFFFF` | `#1A1A1A` |
| `text.link` | 超链接 | `#005FB8` | `#60CDFF` |
| `icon.primary` | 单色图标 | `#3D3D42` | `#D6D6DA` |
| `icon.secondary` | 次要图标 | `#6E6E76` | `#9E9EA6` |

### 2.4 颜色 — 强调色与主操作

产品级二选一，**同一产品只采用一种主按钮风格**：

- **方案 A：强调色主按钮**（Windows 风格）——适合面向普通用户、需要明确引导的工具。
- **方案 B：中性主按钮**（GitLab 风格）——适合开发者工具、管理后台，强调色只留给链接与焦点。

| 令牌 | 浅色 | 深色 |
|---|---|---|
| `accent.default` | `#005FB8` | `#60CDFF` |
| `accent.hover` | `#1A6FC0` | `#5AB8E6` |
| `accent.pressed` | `#2D7CC6` | `#52A4CC` |
| `accent.indicator`（选中竖条、页签下划线） | `#005FB8` | `#60CDFF` |
| `neutral.primary`（方案 B 主按钮） | `#1F1F1F` | `#F2F2F2` |
| `neutral.primary.hover` | `#333333` | `#DADADA` |
| `neutral.primary.pressed` | `#474747` | `#C4C4C4` |

> 采用方案 B 时，导航选中竖条与页签下划线可改用 `text.primary`，与 GitLab 一致；采用方案 A 时使用 `accent.indicator`。

### 2.5 颜色 — 状态

状态必须同时用 **图标形状 + 文字** 表达，颜色只是辅助。

| 令牌 | 前景 浅 / 深 | 背景 浅 / 深 | 图标形状 |
|---|---|---|---|
| `status.neutral`（待办、草稿） | `#5F5F66` / `#B8B8BE` | `#ECECEE` / `#3A3A3A` | 空心圆 ○ |
| `status.info`（进行中） | `#005FB8` / `#60CDFF` | `#E5F0FA` / `#1E3A52` | 半实心圆 ◐ |
| `status.success`（完成、正常） | `#0F7B0F` / `#6CCB5F` | `#DFF6DD` / `#1F3A1D` | 对勾 ✓ |
| `status.warning`（警告、待处理） | `#9D5D00` / `#FCE100` | `#FFF4CE` / `#433519` | 三角 ⚠ |
| `status.danger`（错误、失败、阻塞） | `#C42B1C` / `#FF99A4` | `#FDE7E9` / `#442726` | 叉 ✕ / 八边形 |

### 2.6 字体

**字体族**

| 用途 | Web | WPF |
|---|---|---|
| 界面文字 | `"Segoe UI Variable Text", "Segoe UI", Inter, system-ui, -apple-system, "Microsoft YaHei UI", "PingFang SC", sans-serif` | `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI` |
| 大标题 | `"Segoe UI Variable Display", "Segoe UI", Inter, system-ui, sans-serif` | `Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI` |
| 代码、ID、路径 | `"Cascadia Mono", Consolas, "SF Mono", monospace` | `Cascadia Mono, Consolas` |

> 中、西文混排时务必在回退链中保留中文字体（Microsoft YaHei UI / PingFang SC），避免中文落到宋体。等宽字体只用于真正的代码、哈希、GUID、文件路径，不要用作装饰性小标签。

**字号层级**

| 令牌 | 字号 / 行高 | 字重 | 用途 |
|---|---|---|---|
| `type.caption` | 12 / 16 | 400 | 元信息、补充说明、徽章数字、面包屑 |
| `type.body` | 14 / 20 | 400 | 正文、导航项、列表标题以外的内容、键值对 |
| `type.bodyStrong` | 14 / 20 | 600 | 列表条目标题、卡片标题、选中导航项、表单标签 |
| `type.subtitle` | 20 / 28 | 600 | 岛内分区标题、对话框标题 |
| `type.title` | 28 / 36 | 600 | 页面标题 |
| `type.stat` | 16 / 22 | 600 | 摘要卡片中的数值 |
| `type.statLarge` | 28 / 36 | 600 | 仪表盘大数值（谨慎使用） |

规则：句首大写（Sentence case），不使用全大写标签；同一页面最多出现三个字号层级；页面标题只有一个。

### 2.7 间距（4px 网格）

| 令牌 | 值 | 典型用途 |
|---|---|---|
| `space.0.5` | 2 | 图标与徽章微调 |
| `space.1` | 4 | 同组卡片间距、图标与文字的紧凑间距 |
| `space.2` | 8 | 岛与窗口边缘的间隙、控件内图标间距、令牌之间 |
| `space.3` | 12 | 卡片内元素间距、按钮水平内边距 |
| `space.4` | 16 | 卡片内边距、列表行水平内边距 |
| `space.5` | 20 | 表单字段间距 |
| `space.6` | 24 | 区块之间、岛内左右内边距（紧凑） |
| `space.8` | 32 | 页面标题与内容之间、岛内左右内边距（舒适） |
| `space.10` | 40 | 大区块之间 |
| `space.12` | 48 | 摘要区与详情区之间 |

### 2.8 圆角（按层级递减）

圆角随层级变化，外层更大、内层更小，不要所有元素一个圆角。

| 令牌 | 值 | 用途 |
|---|---|---|
| `radius.island` | 12 | 岛（主面板、侧岛） |
| `radius.card` | 8 | 卡片、展开卡片、对话框、弹出层 |
| `radius.control` | 4 | 按钮、输入框、下拉框、导航项选中块、令牌块 |
| `radius.pill` | 999 | 搜索框、计数徽章、状态胶囊、选中竖条 |
| `radius.avatar` | 50% | 头像 |

### 2.9 阴影与层次

本规范以明度分层为主，阴影只在「浮起」时使用。

| 令牌 | 值（CSS） | 用途 |
|---|---|---|
| `elevation.0` | `none` | 卡片、岛（只用描边） |
| `elevation.island` | `0 1px 2px rgba(0,0,0,.04)` | 岛（浅色模式可选，深色模式不用） |
| `elevation.flyout` | `0 8px 16px rgba(0,0,0,.14)` | 下拉、菜单、提示 |
| `elevation.dialog` | `0 32px 64px rgba(0,0,0,.19), 0 2px 21px rgba(0,0,0,.15)` | 对话框 |

### 2.10 动效

| 令牌 | 值 | 用途 |
|---|---|---|
| `motion.fast` | 83ms | 悬停、按下的颜色变化 |
| `motion.normal` | 167ms | 展开卡片、下拉打开、页签指示器移动 |
| `motion.slow` | 250ms | 侧岛滑入、对话框出现 |
| `easing.decelerate` | `cubic-bezier(0, 0, 0, 1)` | 进入、展开 |
| `easing.accelerate` | `cubic-bezier(1, 0, 1, 1)` | 退出、收起 |
| `easing.standard` | `cubic-bezier(0.8, 0, 0.2, 1)` | 位置移动 |

动效只回应用户操作（展开、打开、切换），不做页面加载时的逐块淡入。必须尊重系统「减少动态效果」设置：Web 用 `prefers-reduced-motion`，WPF 读取 `SystemParameters.ClientAreaAnimation`。

### 2.11 尺寸

| 令牌 | 舒适 | 紧凑 | 说明 |
|---|---|---|---|
| `size.control.height` | 32 | 28 | 按钮、输入框、下拉框 |
| `size.control.heightLarge` | 40 | 32 | 主页面搜索框、重要输入 |
| `size.nav.itemHeight` | 36 | 32 | 侧边导航项 |
| `size.list.rowHeight` | 64 | 52 | 双行列表条目最小高度 |
| `size.setting.rowHeight` | 68 | 56 | 设置卡片最小高度 |
| `size.icon` | 16 | 16 | 界面图标 |
| `size.icon.nav` | 20 | 16 | 导航图标 |
| `size.avatar.sm` | 20 | 16 | 列表内头像 |
| `size.avatar.md` | 32 | 28 | 顶栏头像 |
| `size.avatar.lg` | 64 | 64 | 账户卡片头像 |

---

## 3. 外壳布局（App Shell）

### 3.1 结构

```
┌──────────────────────────────────────────────────────────────────┐
│ [Logo] 应用名        [🔍 搜索或跳转…            Ctrl+K ]   [+] [🔔3] (头像) │  TopBar   h=48   bg.canvas
├────────────┬─────────────────────────────────────────────┬──┬──────┤
│ 上下文标题   │╭───────────────────────────────────────────╮│  │╭────╮│
│ ⌂ 导航项     ││  面包屑                                    ││✎ ││侧岛 ││
│▌▣ 导航项  3  ││                                           ││💬││    ││
│ ⑂ 导航项  1  ││        ┌── 内容列（限宽，居中）──┐          ││⟲ ││    ││
│ …           ││        │ 页面标题        [主操作] │          ││  ││    ││
│             ││        │ 页签 / 过滤 / 列表 / 卡片 │          ││  ││    ││
│             ││        └────────────────────────┘          ││  ││    ││
│ ? 帮助       ││                                           ││  ││    ││
│ ⇤ 收起       │╰───────────────────────────────────────────╯│  │╰────╯│
└────────────┴─────────────────────────────────────────────┴──┴──────┘
   Sidebar            Main Island（bg.island, radius.island）       Rail  Side Island
   w=260/48                                                       w=48  w=360（可选）
```

| 区域 | 尺寸 | 背景 | 说明 |
|---|---|---|---|
| TopBar 顶栏 | 高 48 | `bg.canvas` | 无独立背景与下边线；Web 端为页面顶部，WPF 端与标题栏合并（扩展到标题栏） |
| Sidebar 侧边导航 | 宽 260，收起 48 | `bg.canvas` | 无卡片背景；可收起为仅图标模式 |
| Main Island 主岛 | 自适应 | `bg.island` | 上、右、下与窗口保持 `space.2`（8px）间隙，左侧紧邻侧栏 |
| Rail 右侧工具条 | 宽 48 | `bg.canvas` | 可选；竖排图标按钮，打开侧岛 |
| Side Island 侧岛 | 宽 360（320–480 可拖拽） | `bg.island` | 可选；详情、聊天、属性面板。与主岛间隔 8px |

### 3.2 内容列

岛内的页面内容放在一个限宽、水平居中的内容列里：

| 页面类型 | 最大宽度 | 岛内左右内边距 | 密度 |
|---|---|---|---|
| 设置 / 详情 / 表单 | 1000 | 32 | 舒适 |
| 列表 / 工作台 | 1280 | 24 | 紧凑 |
| 数据表 / 仪表盘 | 不限（100%） | 24 | 紧凑 |

面包屑放在岛的左上角（距岛边缘 16px），不跟随内容列缩进；页面标题及以下内容跟随内容列。

### 3.3 两种外壳变体

**变体 1：岛屿式（默认，GitLab 风格）**——导航在海面，内容在一座白色/深灰大岛上，岛内的卡片用 `bg.card` + `stroke.card`。适合工作台、管理后台、开发工具。

**变体 2：卡片列表式（Windows 设置风格）**——不画主岛，内容直接放在海面上，每个设置项是一张独立卡片（`bg.card`），卡片间距 4px。适合设置页、偏好页、只读信息页。

同一应用中可以混用：外壳统一使用变体 1，「设置」页在岛内按变体 2 的卡片规则排列。

### 3.4 响应式断点

| 断点 | 宽度 | 行为 |
|---|---|---|
| `bp.compact` | < 640 | 侧栏变为抽屉（汉堡菜单打开），主岛取消圆角与间隙铺满，Rail 隐藏，侧岛改为全屏覆盖 |
| `bp.medium` | 640–1007 | 侧栏默认收起为 48 宽图标栏，侧岛以覆盖方式打开 |
| `bp.expanded` | 1008–1439 | 标准布局，侧岛打开时挤压主岛 |
| `bp.wide` | ≥ 1440 | 标准布局，内容列限宽居中 |

WPF 桌面端按窗口宽度使用同样的断点（最小窗口宽度建议 800）。

---

## 4. 组件规格

每个组件按「结构 → 尺寸 → 状态 → 令牌」描述。

### 4.1 顶栏 TopBar

- **结构**：左侧 Logo（24×24）+ 可选应用名（`type.bodyStrong`）；中间全局搜索框；右侧新建按钮、带计数的全局入口、头像。
- **全局搜索框**：宽 480（最小 240，最大 640），高 32，`radius.pill`，背景 `bg.control`，描边 `stroke.control`；左侧 16px 放大镜图标；右侧快捷键提示键帽（`type.caption`，`bg.badge`，`radius.control`，内边距 2×6）。快捷键：`Ctrl+K`（Web 也接受 `/`）。
- **计数入口**：图标按钮 + 右侧数字（`type.caption`，`text.secondary`）；数字为 0 时隐藏。多个入口之间用一条高 20 的 `stroke.divider` 竖线分组。
- **WPF**：顶栏即标题栏，右侧为系统最小化/最大化/关闭按钮，自定义内容放在其左侧。

### 4.2 侧边导航 Sidebar / NavItem

- **上下文标题**（可选）：`type.bodyStrong`，`text.primary`，上下内边距 8，左内边距 16。表示当前所在层级，例如「我的工作」「项目 X」。
- **账户卡**（可选，设置类应用）：头像 64 + 姓名（`type.bodyStrong`）+ 邮箱（`type.caption`，`text.secondary`），无背景。
- **导航项**：
  - 高 `size.nav.itemHeight`，左右外边距 8，内边距左 12 右 12，`radius.control`。
  - 图标 `size.icon.nav`，与文字间距 12。图标风格全应用统一：**默认单色线条图标**（`icon.primary`）；仅当应用以「浏览分类」为主（如设置）时可使用全彩图标。
  - 计数：右对齐，`type.caption`，`text.tertiary`。
  - 有子级：右侧 12px 的 › 箭头，展开后箭头旋转 90°，子项左缩进 32。
- **状态**：

| 状态 | 背景 | 文字 | 指示条 |
|---|---|---|---|
| 默认 | 透明 | `text.primary` / `type.body` | 无 |
| 悬停 | `bg.subtle.hover` | 同上 | 无 |
| 按下 | `bg.subtle.selected` | `text.secondary` | 无 |
| 选中 | `bg.subtle.selected` | `type.bodyStrong` | 左侧竖条：宽 3，高 16，`radius.pill`，垂直居中，颜色见 2.4 |
| 禁用 | 透明 | `text.disabled` | 无 |

- **底部固定区**：帮助、设置、收起侧栏，与上方列表之间留白自动撑开。
- **收起状态**：宽 48，只显示图标，悬停显示 Tooltip；计数改为图标右上角的小圆点徽章。

### 4.3 页面头部 PageHeader

- **面包屑**：`type.caption`；父级 `text.secondary` 可点击，当前级 `text.primary` + 600 字重；分隔符 `/`（工作台类）或 `›`（设置类），全应用统一一种；分隔符两侧间距 6。
  - 设置类页面可以把面包屑直接作为页面标题（`type.title`，父级 `text.secondary`，当前级 `text.primary`，`›` 分隔），此时岛左上角不再放小号面包屑。
- **标题行**：页面标题 `type.title` 左对齐；主操作按钮与标题顶部对齐放在右侧；次要操作放在主按钮左侧，间距 8。
- **描述**（可选）：标题下方 4px，`type.body`，`text.secondary`，最长一行 80 字符以内。
- 标题行与下方内容间距 `space.6`（24）。

### 4.4 按钮 Button

| 类型 | 背景 | 文字 | 描边 | 用途 |
|---|---|---|---|---|
| Primary | 方案 A：`accent.default`；方案 B：`neutral.primary` | `text.onAccent` / `text.onNeutral` | 无 | 每个视图最多一个 |
| Secondary（默认） | `bg.control` | `text.primary` | `stroke.control` | 普通操作（如「复制」「重命名」） |
| Subtle | 透明 | `text.primary` | 无 | 工具栏、卡片头部的低优先级操作 |
| Danger | `status.danger` 前景色 | `#FFFFFF` | 无 | 删除等破坏性操作，需二次确认 |
| Link | 透明 | `text.link` | 无 | 相关链接、行内导航 |

- 尺寸：高 `size.control.height`，水平内边距 12，`radius.control`，文字 `type.body`（Primary 用 600 字重）；带图标时图标 16，间距 8。
- 图标按钮：32×32（紧凑 28×28），Subtle 样式，必须有 Tooltip 与无障碍名称。
- **分体按钮 SplitButton**：主动作区 + 下拉区（宽 32），中间 1px 分隔线（Primary 上用 20% 白色，Secondary 上用 `stroke.control`）；两区共享外圆角。
- **状态**：悬停 → `*.hover`；按下 → `*.pressed`；禁用 → 背景 `bg.badge`、文字 `text.disabled`；加载中 → 文字前加 16px 旋转指示器并禁用点击，按钮宽度保持不变。
- 文案：动词开头，说明会发生什么，如「保存更改」「创建工作项」，不用「确定」「提交」。

### 4.5 页签 Tabs

- 页签项：高 40，水平内边距 12，项间距 4；文字 `type.body`，选中 `type.bodyStrong`。
- 计数徽章：文字后 6px，`type.caption`，`bg.badge`，`radius.pill`，内边距 0×6，最小宽 20。
- 选中指示：底部下划线，高 2（紧凑）或 3（舒适），左右缩进 12，颜色见 2.4；切换时下划线以 `motion.normal` 平移。
- 页签行下方贯穿一条 `stroke.divider` 基线；行最右侧可放 ⋮ 更多菜单。

### 4.6 过滤栏 FilterBar（令牌式搜索）

```
┌─────────────────────────────────────────────────────────────────────────────┐ bg.layer
│ [⟲▾] │ [负责人][=][👤 张三 ×] [状态][≠][已关闭 ×]  输入以添加条件…   ⊗ │🔍│  [创建时间 ▾│⇅] │
└─────────────────────────────────────────────────────────────────────────────┘
```

- 外层色带：`bg.layer`，上下内边距 12，左右内边距 16；在岛内与页签基线相接。
- 组合输入框：高 32，`bg.control`，`stroke.control`，`radius.control`；左侧最近搜索按钮与输入区之间 1px 分隔线；右侧依次为清空按钮（⊗，仅有条件时显示）和搜索按钮。
- **令牌 Token**：由「字段」「运算符」「值」三段组成，每段 `bg.badge`、`type.caption`、高 24、内边距 0×6，段间距 2，整体 `radius.control`；值段可含 16px 头像或图标；末尾 × 删除该条件。令牌可点击重新编辑，可用退格键删除最后一个。
- 输入时弹出建议菜单（`bg.flyout`、`elevation.flyout`、`radius.card`）：先选字段，再选运算符，再选值。
- 排序控件：下拉框（排序字段）与方向切换图标按钮拼接，共享外圆角，与过滤输入框间距 8。

### 4.7 列表条目 ListRow（紧凑双行）

```
┌─────────────────────────────────────────────────────────────────┐
│ ▢  条目标题（bodyStrong）                          (👤)  [○ 待办] │
│    项目/路径#7 · 创建于 2025-05-12 · 张三（caption, secondary）     │
└─────────────────────────────────────────────────────────────────┘
```

- 高度：最小 `size.list.rowHeight`；内边距 上下 12、左右 16。
- 左侧类型图标 16（`icon.secondary`），与标题间距 8；元信息行与标题左对齐。
- 标题：`type.bodyStrong`，单行省略；可点击时悬停加下划线。
- 元信息：`type.caption`，`text.secondary`，字段之间用 ` · ` 分隔；ID、路径可用等宽字体。
- 右侧区：头像组、标签、状态徽章，右对齐、垂直居中，间距 8。
- 行间分隔：`stroke.divider` 1px；行悬停 `bg.subtle.hover`；选中 `bg.subtle.selected`；键盘焦点显示焦点环（内缩 2）。
- 空状态：居中插图（可选）+ 一句说明当前为什么是空的 + 一个直接可执行的主操作按钮。

### 4.8 状态徽章 StatusBadge

- 高 20，内边距 0×8，`radius.pill`，`type.caption`；左侧 12px 状态图标，间距 4。
- 颜色取自 2.5，背景用对应状态背景色。
- 文案用名词或形容词，统一大小写，如「待办」「进行中」「已完成」。

### 4.9 摘要卡片 StatCard（Windows「信息」页风格）

```
┌──────────────────────┐
│ ▣ 内存（caption）     │
│ 32.0 GB（stat）       │
│                      │
│ 速度 5600 MT/s（caption, secondary，贴底） │
└──────────────────────┘
```

- 固定宽 196，最小高 152，内边距 16，`bg.card`，`stroke.card`，`radius.card`。
- 三段结构：顶部「图标 16 + 标签」；中部数值 `type.stat`（允许换行，最多 3 行）；底部补充信息贴底。
- 一行多张时间距 12，左对齐、不拉伸；窄屏换行。
- 只在数值是页面最重要信息时使用，不要把所有信息都切成摘要卡片。

### 4.10 设置卡片 SettingCard

```
┌──────────────────────────────────────────────────────────────┐
│ ⓘ  标题（body）                                [控件区]   ›    │
│    说明文字（caption, secondary）                               │
└──────────────────────────────────────────────────────────────┘
```

- 最小高 `size.setting.rowHeight`，内边距 左右 16、上下 12；`bg.card`，`stroke.card`，`radius.card`。
- 左侧图标 20（可选），与文字间距 16；标题 `type.body`，说明 `type.caption` + `text.secondary`。
- 右侧控件区：开关、下拉框、按钮，右对齐，与右边缘 16；可导航的卡片末尾加 › 箭头，整卡可点击，悬停 `bg.control.hover`。
- 同组卡片间距 `space.1`（4）；组标题（`type.bodyStrong`）位于组上方，距组 8，距上一组 24。

### 4.11 展开卡片 ExpanderCard

- 头部同 SettingCard；右侧为操作按钮（如「复制」，Secondary）+ 展开箭头（˅ / ˄，图标按钮 32）。
- 展开区：与头部之间 `stroke.divider` 1px；背景 `bg.card`（Windows 风格）或 `bg.layer`（需要强调层次时）；内边距 左 52（与标题文字对齐）、右 16、上下 12。
- 展开 / 收起：高度以 `motion.normal` + `easing.decelerate` 过渡，箭头旋转 180°。
- 底部附加区（可选）：如「相关链接」，与上方用 `stroke.divider` 分隔；左侧 `type.bodyStrong` 小标题，右侧横排 Link 按钮，间距 24。

### 4.12 键值列表 KeyValueList

- 两列网格：键列固定宽（默认 232，或取最长键的宽度），值列自适应。
- 键：`type.body`，`text.primary`；值：`type.body`，`text.secondary`，可选中复制。
- 行高 24–28，无分隔线、无斑马纹。
- 值过长时换行而非省略；ID、GUID、版本号可用等宽字体。
- 整组提供「复制」操作时，复制内容为 `键: 值` 每行一条的纯文本。

### 4.13 输入控件

- 文本框：高 32，`bg.control`，`stroke.control`，`radius.control`，内边距 0×10；底边额外 1px `stroke.control.strong`（Fluent 风格）；聚焦时底边变为 2px 强调色（方案 A）或 `text.primary`（方案 B）。
- 标签在上方，`type.body`，距输入框 6；说明 / 错误信息在下方，`type.caption`，错误用 `status.danger` 前景并附带图标。
- 下拉框、日期选择、数字框遵循同样尺寸与状态。
- 开关 Toggle：40×20，打开时轨道为强调色（方案 B 用 `neutral.primary`），右侧可带状态文字「开 / 关」。

### 4.14 弹出层与对话框

- 菜单 / 下拉：`bg.flyout`，`stroke.card`，`radius.card`，`elevation.flyout`，内边距 4；菜单项高 32、`radius.control`、悬停 `bg.subtle.hover`。
- 对话框：宽 480（小）/ 640（中），`radius.card`，`elevation.dialog`，遮罩 `bg.overlay`；内边距 24；标题 `type.subtitle`；底部按钮区 `bg.layer`，右对齐，主按钮在最右。
- Tooltip：`bg.flyout`，`type.caption`，`radius.control`，延迟 500ms 出现。

### 4.15 侧岛 SidePanel

- 通过 Rail 图标按钮或条目上的「详情」打开；宽 360 默认，可拖拽 320–480。
- 头部：标题 `type.bodyStrong` + 右侧关闭按钮，高 48，下方 `stroke.divider`。
- 打开动画：从右侧滑入 + 淡入，`motion.slow` + `easing.decelerate`；关闭用 `easing.accelerate`。
- Rail 中对应图标在侧岛打开期间保持选中背景 `bg.subtle.selected`。

---

## 5. 图标

- 统一使用一套线条图标库：Web 推荐 Fluent UI System Icons（Regular 线条，选中态可用 Filled）；WPF 使用 WPF UI 自带的 `SymbolIcon`（同一套 Fluent 图标）或 Segoe Fluent Icons 字体。
- 尺寸只用 16 / 20 / 24；描边粗细与字重协调，不混用不同图标库。
- 选中 / 激活态可从 Regular 切换为 Filled，以形状变化而不是仅颜色变化表达状态。
- 纯图标按钮必须提供 Tooltip 与无障碍名称（Web `aria-label`，WPF `AutomationProperties.Name`）。

---

## 6. 可访问性

- 文字对比度：正文 ≥ 4.5:1，大字号（≥ 18.66px 粗体或 24px）≥ 3:1；`text.tertiary` 只用于非关键信息。
- 焦点环：双层，外圈 2px `stroke.focus.outer`、内圈 1px `stroke.focus.inner`，圆角比元素大 2；只在键盘操作时显示（Web `:focus-visible`，WPF `FocusVisualStyle`）。
- 所有功能可键盘完成：`Tab` 顺序符合视觉顺序；全局搜索 `Ctrl+K`；对话框 `Esc` 关闭、焦点锁定在对话框内；列表支持方向键移动。
- 状态不只靠颜色（见 2.5）。
- 支持系统缩放与 Windows 高对比度主题：WPF 端在高对比度下回退到 `SystemColors`；Web 端用 `forced-colors` 媒体查询保留边框。
- 尊重减少动态效果设置（见 2.10）。

---

## 7. Web 落地

### 7.1 令牌（CSS 变量）

```css
/* tokens.css */
:root {
  color-scheme: light;
  /* 表面 */
  --ui-bg-canvas: #F3F3F3;
  --ui-bg-island: #FFFFFF;
  --ui-bg-card: #FFFFFF;
  --ui-bg-layer: #F7F7F8;
  --ui-bg-control: #FFFFFF;
  --ui-bg-control-hover: #F5F5F5;
  --ui-bg-control-pressed: #EDEDED;
  --ui-bg-subtle-hover: #0000000A;
  --ui-bg-subtle-selected: #0000000F;
  --ui-bg-badge: #ECECEE;
  --ui-bg-overlay: #00000066;
  --ui-bg-flyout: #FFFFFF;
  /* 描边 */
  --ui-stroke-island: #E5E5E5;
  --ui-stroke-card: #E5E5E5;
  --ui-stroke-divider: #EBEBEB;
  --ui-stroke-control: #D6D6D6;
  --ui-stroke-control-strong: #8A8A8A;
  --ui-stroke-focus-outer: #1A1A1A;
  --ui-stroke-focus-inner: #FFFFFF;
  /* 文字 */
  --ui-text-primary: #1A1A1A;
  --ui-text-secondary: #5F5F66;
  --ui-text-tertiary: #8B8B93;
  --ui-text-disabled: #A8A8AE;
  --ui-text-on-accent: #FFFFFF;
  --ui-text-on-neutral: #FFFFFF;
  --ui-text-link: #005FB8;
  --ui-icon-primary: #3D3D42;
  --ui-icon-secondary: #6E6E76;
  /* 强调与主操作 */
  --ui-accent: #005FB8;
  --ui-accent-hover: #1A6FC0;
  --ui-accent-pressed: #2D7CC6;
  --ui-neutral-primary: #1F1F1F;
  --ui-neutral-primary-hover: #333333;
  --ui-neutral-primary-pressed: #474747;
  /* 选择方案：A 用 accent，B 用 neutral */
  --ui-primary: var(--ui-accent);
  --ui-primary-hover: var(--ui-accent-hover);
  --ui-primary-pressed: var(--ui-accent-pressed);
  --ui-on-primary: var(--ui-text-on-accent);
  --ui-indicator: var(--ui-accent);
  /* 状态 */
  --ui-status-neutral-fg: #5F5F66;  --ui-status-neutral-bg: #ECECEE;
  --ui-status-info-fg: #005FB8;     --ui-status-info-bg: #E5F0FA;
  --ui-status-success-fg: #0F7B0F;  --ui-status-success-bg: #DFF6DD;
  --ui-status-warning-fg: #9D5D00;  --ui-status-warning-bg: #FFF4CE;
  --ui-status-danger-fg: #C42B1C;   --ui-status-danger-bg: #FDE7E9;

  /* 字体 */
  --ui-font: "Segoe UI Variable Text", "Segoe UI", Inter, system-ui, -apple-system,
             "Microsoft YaHei UI", "PingFang SC", sans-serif;
  --ui-font-display: "Segoe UI Variable Display", "Segoe UI", Inter, system-ui,
             "Microsoft YaHei UI", "PingFang SC", sans-serif;
  --ui-font-mono: "Cascadia Mono", Consolas, "SF Mono", monospace;

  /* 间距、圆角、尺寸 */
  --ui-space-1: 4px;  --ui-space-2: 8px;  --ui-space-3: 12px; --ui-space-4: 16px;
  --ui-space-5: 20px; --ui-space-6: 24px; --ui-space-8: 32px; --ui-space-10: 40px;
  --ui-space-12: 48px;
  --ui-radius-island: 12px; --ui-radius-card: 8px; --ui-radius-control: 4px;
  --ui-radius-pill: 999px;
  --ui-control-height: 32px;

  /* 阴影与动效 */
  --ui-elevation-island: 0 1px 2px rgba(0,0,0,.04);
  --ui-elevation-flyout: 0 8px 16px rgba(0,0,0,.14);
  --ui-elevation-dialog: 0 32px 64px rgba(0,0,0,.19), 0 2px 21px rgba(0,0,0,.15);
  --ui-motion-fast: 83ms; --ui-motion-normal: 167ms; --ui-motion-slow: 250ms;
  --ui-ease-decelerate: cubic-bezier(0, 0, 0, 1);
  --ui-ease-accelerate: cubic-bezier(1, 0, 1, 1);
}

/* 方案 B：中性主按钮 —— 在 <html data-primary="neutral"> 上启用 */
:root[data-primary="neutral"] {
  --ui-primary: var(--ui-neutral-primary);
  --ui-primary-hover: var(--ui-neutral-primary-hover);
  --ui-primary-pressed: var(--ui-neutral-primary-pressed);
  --ui-on-primary: var(--ui-text-on-neutral);
  --ui-indicator: var(--ui-text-primary);
}

/* 深色：由 data-theme="dark" 启用（启动脚本见下方说明） */
:root[data-theme="dark"] {
  color-scheme: dark;
  --ui-bg-canvas: #1C1C1C;
  --ui-bg-island: #242424;
  --ui-bg-card: #2B2B2B;
  --ui-bg-layer: #202020;
  --ui-bg-control: #333333;
  --ui-bg-control-hover: #3A3A3A;
  --ui-bg-control-pressed: #2F2F2F;
  --ui-bg-subtle-hover: #FFFFFF0F;
  --ui-bg-subtle-selected: #FFFFFF14;
  --ui-bg-badge: #3A3A3A;
  --ui-bg-overlay: #0000008C;
  --ui-bg-flyout: #2C2C2C;
  --ui-stroke-island: #FFFFFF0F;
  --ui-stroke-card: #FFFFFF12;
  --ui-stroke-divider: #FFFFFF14;
  --ui-stroke-control: #FFFFFF1F;
  --ui-stroke-control-strong: #9A9A9A;
  --ui-stroke-focus-outer: #FFFFFF;
  --ui-stroke-focus-inner: #000000;
  --ui-text-primary: #F2F2F2;
  --ui-text-secondary: #B8B8BE;
  --ui-text-tertiary: #8E8E96;
  --ui-text-disabled: #6B6B72;
  --ui-text-on-accent: #000000;
  --ui-text-on-neutral: #1A1A1A;
  --ui-text-link: #60CDFF;
  --ui-icon-primary: #D6D6DA;
  --ui-icon-secondary: #9E9EA6;
  --ui-accent: #60CDFF;
  --ui-accent-hover: #5AB8E6;
  --ui-accent-pressed: #52A4CC;
  --ui-neutral-primary: #F2F2F2;
  --ui-neutral-primary-hover: #DADADA;
  --ui-neutral-primary-pressed: #C4C4C4;
  --ui-status-neutral-fg: #B8B8BE;  --ui-status-neutral-bg: #3A3A3A;
  --ui-status-info-fg: #60CDFF;     --ui-status-info-bg: #1E3A52;
  --ui-status-success-fg: #6CCB5F;  --ui-status-success-bg: #1F3A1D;
  --ui-status-warning-fg: #FCE100;  --ui-status-warning-bg: #433519;
  --ui-status-danger-fg: #FF99A4;   --ui-status-danger-bg: #442726;
  --ui-elevation-island: none;
}

@media (prefers-reduced-motion: reduce) {
  :root { --ui-motion-fast: 0ms; --ui-motion-normal: 0ms; --ui-motion-slow: 0ms; }
}
```

> 跟随系统主题：在 `<head>` 中尽早执行下面的脚本（避免首屏闪烁），用户手动选择的主题优先。
>
> ```html
> <script>
>   (function () {
>     var saved = null;
>     try { saved = localStorage.getItem('ui-theme'); } catch (e) {}
>     var dark = saved ? saved === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
>     document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
>   })();
> </script>
> ```

### 7.2 外壳

```html
<body class="ui-shell">
  <header class="ui-topbar">…</header>
  <nav class="ui-sidebar">…</nav>
  <main class="ui-island">
    <div class="ui-breadcrumb">…</div>
    <div class="ui-content ui-content--workspace">…</div>
  </main>
  <aside class="ui-rail">…</aside>
</body>
```

```css
html, body { height: 100%; margin: 0; }
.ui-shell {
  display: grid;
  grid-template-columns: 260px minmax(0, 1fr) 48px;
  grid-template-rows: 48px minmax(0, 1fr);
  grid-template-areas: "top top top" "nav main rail";
  background: var(--ui-bg-canvas);
  color: var(--ui-text-primary);
  font: 400 14px/20px var(--ui-font);
}
.ui-topbar  { grid-area: top; display: flex; align-items: center; gap: 8px; padding: 0 12px; }
.ui-sidebar { grid-area: nav; overflow-y: auto; padding: 4px 0; }
.ui-rail    { grid-area: rail; display: flex; flex-direction: column; align-items: center; gap: 4px; }
.ui-island {
  grid-area: main;
  margin: 0 0 8px;                   /* 右侧间隙由 rail 列提供 */
  background: var(--ui-bg-island);
  border: 1px solid var(--ui-stroke-island);
  border-radius: var(--ui-radius-island);
  box-shadow: var(--ui-elevation-island);
  overflow: auto;
}
.ui-content { margin: 0 auto; padding: 0 var(--ui-space-6) var(--ui-space-8); }
.ui-content--settings  { max-width: 1000px; padding-inline: var(--ui-space-8); }
.ui-content--workspace { max-width: 1280px; }

/* 无 rail 时 */
.ui-shell--no-rail { grid-template-columns: 260px minmax(0, 1fr); grid-template-areas: "top top" "nav main"; }
.ui-shell--no-rail .ui-island { margin-right: 8px; }

/* 侧栏收起 */
.ui-shell--collapsed { grid-template-columns: 48px minmax(0, 1fr) 48px; }

/* 窄屏 */
@media (max-width: 639px) {
  .ui-shell { grid-template-columns: minmax(0, 1fr); grid-template-areas: "top" "main"; }
  .ui-sidebar, .ui-rail { display: none; }   /* 侧栏改为抽屉 */
  .ui-island { margin: 0; border-radius: 0; border-inline: 0; }
}
```

### 7.3 关键组件示例

```css
/* 导航项 */
.ui-nav-item {
  position: relative; display: flex; align-items: center; gap: 12px;
  height: 36px; margin: 0 8px; padding: 0 12px;
  border-radius: var(--ui-radius-control);
  color: var(--ui-text-primary); text-decoration: none;
  transition: background-color var(--ui-motion-fast);
}
.ui-nav-item:hover { background: var(--ui-bg-subtle-hover); }
.ui-nav-item[aria-current="page"] { background: var(--ui-bg-subtle-selected); font-weight: 600; }
.ui-nav-item[aria-current="page"]::before {
  content: ""; position: absolute; left: 0; top: 50%; translate: 0 -50%;
  width: 3px; height: 16px; border-radius: var(--ui-radius-pill); background: var(--ui-indicator);
}
.ui-nav-item .count { margin-left: auto; font-size: 12px; color: var(--ui-text-tertiary); }

/* 按钮 */
.ui-btn {
  display: inline-flex; align-items: center; gap: 8px;
  height: var(--ui-control-height); padding: 0 12px;
  border: 1px solid var(--ui-stroke-control); border-radius: var(--ui-radius-control);
  background: var(--ui-bg-control); color: var(--ui-text-primary); font: inherit; cursor: pointer;
  transition: background-color var(--ui-motion-fast);
}
.ui-btn:hover  { background: var(--ui-bg-control-hover); }
.ui-btn:active { background: var(--ui-bg-control-pressed); }
.ui-btn--primary { background: var(--ui-primary); color: var(--ui-on-primary); border-color: transparent; font-weight: 600; }
.ui-btn--primary:hover  { background: var(--ui-primary-hover); }
.ui-btn--primary:active { background: var(--ui-primary-pressed); }

/* 焦点环（全局） */
:focus-visible {
  outline: 2px solid var(--ui-stroke-focus-outer);
  outline-offset: 1px;
  box-shadow: 0 0 0 1px var(--ui-stroke-focus-inner);
}

/* 设置卡片与卡片组 */
.ui-card-group { display: flex; flex-direction: column; gap: var(--ui-space-1); }
.ui-setting-card {
  display: flex; align-items: center; gap: 16px;
  min-height: 68px; padding: 12px 16px;
  background: var(--ui-bg-card); border: 1px solid var(--ui-stroke-card);
  border-radius: var(--ui-radius-card);
}
.ui-setting-card .desc { font-size: 12px; line-height: 16px; color: var(--ui-text-secondary); }
.ui-setting-card .actions { margin-left: auto; display: flex; gap: 8px; align-items: center; }

/* 键值列表 */
.ui-kv { display: grid; grid-template-columns: 232px 1fr; row-gap: 6px; column-gap: 16px; }
.ui-kv dt { color: var(--ui-text-primary); }
.ui-kv dd { margin: 0; color: var(--ui-text-secondary); overflow-wrap: anywhere; }

/* 状态徽章 */
.ui-status {
  display: inline-flex; align-items: center; gap: 4px; height: 20px; padding: 0 8px;
  border-radius: var(--ui-radius-pill); font-size: 12px;
  color: var(--ui-status-neutral-fg); background: var(--ui-status-neutral-bg);
}
.ui-status--success { color: var(--ui-status-success-fg); background: var(--ui-status-success-bg); }
.ui-status--danger  { color: var(--ui-status-danger-fg);  background: var(--ui-status-danger-bg); }
```

> **Blazor 提示**：使用 Fluent UI Blazor 时，组件本身已带 Fluent 样式，本规范的令牌主要用于外壳（`ui-shell`、`ui-island`、`ui-sidebar`）和自定义组件；可把 `--ui-accent` 与 Fluent UI Blazor 的强调色设置保持一致，避免两套蓝色并存。

---

## 8. WPF 落地

### 8.1 文件组织

```
Themes/
├─ Tokens.xaml        // 与主题无关：间距、圆角、字号、字体、尺寸
├─ Light.xaml         // 浅色画刷
├─ Dark.xaml          // 深色画刷（键名与 Light.xaml 完全一致）
└─ Controls.xaml      // Island、Card、NavItem 等样式（全部用 DynamicResource 引用画刷）
```

`App.xaml`：

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <!-- 如果使用 WPF UI，先合并它的主题与控件字典 -->
            <ui:ThemesDictionary Theme="Light" />
            <ui:ControlsDictionary />
            <!-- 本规范 -->
            <ResourceDictionary Source="Themes/Tokens.xaml" />
            <ResourceDictionary Source="Themes/Light.xaml" />   <!-- 运行时替换为 Dark.xaml -->
            <ResourceDictionary Source="Themes/Controls.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

### 8.2 Tokens.xaml

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">
    <!-- 字体 -->
    <FontFamily x:Key="Ui.Font">Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI</FontFamily>
    <FontFamily x:Key="Ui.Font.Display">Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI</FontFamily>
    <FontFamily x:Key="Ui.Font.Mono">Cascadia Mono, Consolas</FontFamily>

    <!-- 字号 -->
    <sys:Double x:Key="Ui.Type.Caption">12</sys:Double>
    <sys:Double x:Key="Ui.Type.Body">14</sys:Double>
    <sys:Double x:Key="Ui.Type.Subtitle">20</sys:Double>
    <sys:Double x:Key="Ui.Type.Title">28</sys:Double>
    <sys:Double x:Key="Ui.Type.Stat">16</sys:Double>

    <!-- 圆角 -->
    <CornerRadius x:Key="Ui.Radius.Island">12</CornerRadius>
    <CornerRadius x:Key="Ui.Radius.Card">8</CornerRadius>
    <CornerRadius x:Key="Ui.Radius.Control">4</CornerRadius>
    <CornerRadius x:Key="Ui.Radius.Pill">999</CornerRadius>

    <!-- 间距（常用 Thickness 预设） -->
    <Thickness x:Key="Ui.Island.Margin">0,0,8,8</Thickness>
    <Thickness x:Key="Ui.Card.Padding">16,12</Thickness>
    <Thickness x:Key="Ui.Content.Padding.Settings">32,0,32,32</Thickness>
    <Thickness x:Key="Ui.Content.Padding.Workspace">24,0,24,32</Thickness>
    <sys:Double x:Key="Ui.Space.CardGap">4</sys:Double>

    <!-- 尺寸 -->
    <sys:Double x:Key="Ui.Size.TopBar">48</sys:Double>
    <sys:Double x:Key="Ui.Size.Sidebar">260</sys:Double>
    <sys:Double x:Key="Ui.Size.SidebarCollapsed">48</sys:Double>
    <sys:Double x:Key="Ui.Size.Rail">48</sys:Double>
    <sys:Double x:Key="Ui.Size.SideIsland">360</sys:Double>
    <sys:Double x:Key="Ui.Size.ControlHeight">32</sys:Double>
    <sys:Double x:Key="Ui.Size.NavItemHeight">36</sys:Double>
    <sys:Double x:Key="Ui.Size.SettingRow">68</sys:Double>
    <sys:Double x:Key="Ui.Content.MaxWidth.Settings">1000</sys:Double>
    <sys:Double x:Key="Ui.Content.MaxWidth.Workspace">1280</sys:Double>
</ResourceDictionary>
```

### 8.3 Light.xaml / Dark.xaml

注意 WPF 颜色格式为 `#AARRGGBB`。

```xml
<!-- Light.xaml -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="Ui.Bg.Canvas"          Color="#F3F3F3" />
    <SolidColorBrush x:Key="Ui.Bg.Island"          Color="#FFFFFF" />
    <SolidColorBrush x:Key="Ui.Bg.Card"            Color="#FFFFFF" />
    <SolidColorBrush x:Key="Ui.Bg.Layer"           Color="#F7F7F8" />
    <SolidColorBrush x:Key="Ui.Bg.Control"         Color="#FFFFFF" />
    <SolidColorBrush x:Key="Ui.Bg.Control.Hover"   Color="#F5F5F5" />
    <SolidColorBrush x:Key="Ui.Bg.Control.Pressed" Color="#EDEDED" />
    <SolidColorBrush x:Key="Ui.Bg.Subtle.Hover"    Color="#0A000000" />
    <SolidColorBrush x:Key="Ui.Bg.Subtle.Selected" Color="#0F000000" />
    <SolidColorBrush x:Key="Ui.Bg.Badge"           Color="#ECECEE" />
    <SolidColorBrush x:Key="Ui.Bg.Overlay"         Color="#66000000" />
    <SolidColorBrush x:Key="Ui.Bg.Flyout"          Color="#FFFFFF" />

    <SolidColorBrush x:Key="Ui.Stroke.Island"        Color="#E5E5E5" />
    <SolidColorBrush x:Key="Ui.Stroke.Card"          Color="#E5E5E5" />
    <SolidColorBrush x:Key="Ui.Stroke.Divider"       Color="#EBEBEB" />
    <SolidColorBrush x:Key="Ui.Stroke.Control"       Color="#D6D6D6" />
    <SolidColorBrush x:Key="Ui.Stroke.Control.Strong" Color="#8A8A8A" />
    <SolidColorBrush x:Key="Ui.Stroke.Focus.Outer"   Color="#1A1A1A" />
    <SolidColorBrush x:Key="Ui.Stroke.Focus.Inner"   Color="#FFFFFF" />

    <SolidColorBrush x:Key="Ui.Text.Primary"   Color="#1A1A1A" />
    <SolidColorBrush x:Key="Ui.Text.Secondary" Color="#5F5F66" />
    <SolidColorBrush x:Key="Ui.Text.Tertiary"  Color="#8B8B93" />
    <SolidColorBrush x:Key="Ui.Text.Disabled"  Color="#A8A8AE" />
    <SolidColorBrush x:Key="Ui.Text.Link"      Color="#005FB8" />
    <SolidColorBrush x:Key="Ui.Icon.Primary"   Color="#3D3D42" />
    <SolidColorBrush x:Key="Ui.Icon.Secondary" Color="#6E6E76" />

    <!-- 主操作：方案 A（强调色）。改用方案 B 时替换为 neutral 值 #1F1F1F / #333333 / #474747，OnPrimary 为 #FFFFFF，Indicator 为 #1A1A1A -->
    <SolidColorBrush x:Key="Ui.Primary"         Color="#005FB8" />
    <SolidColorBrush x:Key="Ui.Primary.Hover"   Color="#1A6FC0" />
    <SolidColorBrush x:Key="Ui.Primary.Pressed" Color="#2D7CC6" />
    <SolidColorBrush x:Key="Ui.OnPrimary"       Color="#FFFFFF" />
    <SolidColorBrush x:Key="Ui.Indicator"       Color="#005FB8" />

    <SolidColorBrush x:Key="Ui.Status.Neutral.Fg" Color="#5F5F66" />
    <SolidColorBrush x:Key="Ui.Status.Neutral.Bg" Color="#ECECEE" />
    <SolidColorBrush x:Key="Ui.Status.Info.Fg"    Color="#005FB8" />
    <SolidColorBrush x:Key="Ui.Status.Info.Bg"    Color="#E5F0FA" />
    <SolidColorBrush x:Key="Ui.Status.Success.Fg" Color="#0F7B0F" />
    <SolidColorBrush x:Key="Ui.Status.Success.Bg" Color="#DFF6DD" />
    <SolidColorBrush x:Key="Ui.Status.Warning.Fg" Color="#9D5D00" />
    <SolidColorBrush x:Key="Ui.Status.Warning.Bg" Color="#FFF4CE" />
    <SolidColorBrush x:Key="Ui.Status.Danger.Fg"  Color="#C42B1C" />
    <SolidColorBrush x:Key="Ui.Status.Danger.Bg"  Color="#FDE7E9" />
</ResourceDictionary>
```

```xml
<!-- Dark.xaml：键名与 Light.xaml 一一对应 -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="Ui.Bg.Canvas"          Color="#1C1C1C" />
    <SolidColorBrush x:Key="Ui.Bg.Island"          Color="#242424" />
    <SolidColorBrush x:Key="Ui.Bg.Card"            Color="#2B2B2B" />
    <SolidColorBrush x:Key="Ui.Bg.Layer"           Color="#202020" />
    <SolidColorBrush x:Key="Ui.Bg.Control"         Color="#333333" />
    <SolidColorBrush x:Key="Ui.Bg.Control.Hover"   Color="#3A3A3A" />
    <SolidColorBrush x:Key="Ui.Bg.Control.Pressed" Color="#2F2F2F" />
    <SolidColorBrush x:Key="Ui.Bg.Subtle.Hover"    Color="#0FFFFFFF" />
    <SolidColorBrush x:Key="Ui.Bg.Subtle.Selected" Color="#14FFFFFF" />
    <SolidColorBrush x:Key="Ui.Bg.Badge"           Color="#3A3A3A" />
    <SolidColorBrush x:Key="Ui.Bg.Overlay"         Color="#8C000000" />
    <SolidColorBrush x:Key="Ui.Bg.Flyout"          Color="#2C2C2C" />

    <SolidColorBrush x:Key="Ui.Stroke.Island"        Color="#0FFFFFFF" />
    <SolidColorBrush x:Key="Ui.Stroke.Card"          Color="#12FFFFFF" />
    <SolidColorBrush x:Key="Ui.Stroke.Divider"       Color="#14FFFFFF" />
    <SolidColorBrush x:Key="Ui.Stroke.Control"       Color="#1FFFFFFF" />
    <SolidColorBrush x:Key="Ui.Stroke.Control.Strong" Color="#9A9A9A" />
    <SolidColorBrush x:Key="Ui.Stroke.Focus.Outer"   Color="#FFFFFF" />
    <SolidColorBrush x:Key="Ui.Stroke.Focus.Inner"   Color="#000000" />

    <SolidColorBrush x:Key="Ui.Text.Primary"   Color="#F2F2F2" />
    <SolidColorBrush x:Key="Ui.Text.Secondary" Color="#B8B8BE" />
    <SolidColorBrush x:Key="Ui.Text.Tertiary"  Color="#8E8E96" />
    <SolidColorBrush x:Key="Ui.Text.Disabled"  Color="#6B6B72" />
    <SolidColorBrush x:Key="Ui.Text.Link"      Color="#60CDFF" />
    <SolidColorBrush x:Key="Ui.Icon.Primary"   Color="#D6D6DA" />
    <SolidColorBrush x:Key="Ui.Icon.Secondary" Color="#9E9EA6" />

    <!-- 方案 A；方案 B 时为 #F2F2F2 / #DADADA / #C4C4C4，OnPrimary #1A1A1A，Indicator #F2F2F2 -->
    <SolidColorBrush x:Key="Ui.Primary"         Color="#60CDFF" />
    <SolidColorBrush x:Key="Ui.Primary.Hover"   Color="#5AB8E6" />
    <SolidColorBrush x:Key="Ui.Primary.Pressed" Color="#52A4CC" />
    <SolidColorBrush x:Key="Ui.OnPrimary"       Color="#000000" />
    <SolidColorBrush x:Key="Ui.Indicator"       Color="#60CDFF" />

    <SolidColorBrush x:Key="Ui.Status.Neutral.Fg" Color="#B8B8BE" />
    <SolidColorBrush x:Key="Ui.Status.Neutral.Bg" Color="#3A3A3A" />
    <SolidColorBrush x:Key="Ui.Status.Info.Fg"    Color="#60CDFF" />
    <SolidColorBrush x:Key="Ui.Status.Info.Bg"    Color="#1E3A52" />
    <SolidColorBrush x:Key="Ui.Status.Success.Fg" Color="#6CCB5F" />
    <SolidColorBrush x:Key="Ui.Status.Success.Bg" Color="#1F3A1D" />
    <SolidColorBrush x:Key="Ui.Status.Warning.Fg" Color="#FCE100" />
    <SolidColorBrush x:Key="Ui.Status.Warning.Bg" Color="#433519" />
    <SolidColorBrush x:Key="Ui.Status.Danger.Fg"  Color="#FF99A4" />
    <SolidColorBrush x:Key="Ui.Status.Danger.Bg"  Color="#442726" />
</ResourceDictionary>
```

### 8.4 主题切换

```csharp
public static class UiTheme
{
    public static void Apply(bool dark)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source is { } s &&
            (s.OriginalString.EndsWith("Light.xaml") || s.OriginalString.EndsWith("Dark.xaml")));
        var index = old is null ? dicts.Count : dicts.IndexOf(old);
        if (old is not null) dicts.Remove(old);
        dicts.Insert(index, new ResourceDictionary
        {
            Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        });

        // 使用 WPF UI 时同步其主题：
        // Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
        //     dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
    }
}
```

所有画刷都必须用 `{DynamicResource ...}` 引用，`StaticResource` 不会随主题切换更新。

### 8.5 Controls.xaml 关键样式

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- 岛 -->
    <Style x:Key="Ui.Island" TargetType="Border">
        <Setter Property="Background"      Value="{DynamicResource Ui.Bg.Island}" />
        <Setter Property="BorderBrush"     Value="{DynamicResource Ui.Stroke.Island}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius"    Value="{StaticResource Ui.Radius.Island}" />
        <Setter Property="Margin"          Value="{StaticResource Ui.Island.Margin}" />
    </Style>

    <!-- 卡片 -->
    <Style x:Key="Ui.Card" TargetType="Border">
        <Setter Property="Background"      Value="{DynamicResource Ui.Bg.Card}" />
        <Setter Property="BorderBrush"     Value="{DynamicResource Ui.Stroke.Card}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius"    Value="{StaticResource Ui.Radius.Card}" />
        <Setter Property="Padding"         Value="{StaticResource Ui.Card.Padding}" />
        <Setter Property="MinHeight"       Value="{StaticResource Ui.Size.SettingRow}" />
    </Style>

    <!-- 文字层级 -->
    <Style x:Key="Ui.Text.Title" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Ui.Font.Display}" />
        <Setter Property="FontSize"   Value="{StaticResource Ui.Type.Title}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource Ui.Text.Primary}" />
    </Style>
    <Style x:Key="Ui.Text.BodyStrong" TargetType="TextBlock">
        <Setter Property="FontSize"   Value="{StaticResource Ui.Type.Body}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource Ui.Text.Primary}" />
    </Style>
    <Style x:Key="Ui.Text.Caption" TargetType="TextBlock">
        <Setter Property="FontSize"   Value="{StaticResource Ui.Type.Caption}" />
        <Setter Property="Foreground" Value="{DynamicResource Ui.Text.Secondary}" />
    </Style>

    <!-- 导航项（ListBoxItem 作为导航） -->
    <Style x:Key="Ui.NavItem" TargetType="ListBoxItem">
        <Setter Property="Height" Value="{StaticResource Ui.Size.NavItemHeight}" />
        <Setter Property="Margin" Value="8,0" />
        <Setter Property="Foreground" Value="{DynamicResource Ui.Text.Primary}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Grid>
                        <Border x:Name="Bg" CornerRadius="{StaticResource Ui.Radius.Control}"
                                Background="Transparent" Padding="12,0">
                            <ContentPresenter VerticalAlignment="Center" />
                        </Border>
                        <Border x:Name="Indicator" Width="3" Height="16" HorizontalAlignment="Left"
                                CornerRadius="{StaticResource Ui.Radius.Pill}"
                                Background="{DynamicResource Ui.Indicator}" Visibility="Collapsed" />
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bg" Property="Background" Value="{DynamicResource Ui.Bg.Subtle.Hover}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bg" Property="Background" Value="{DynamicResource Ui.Bg.Subtle.Selected}" />
                            <Setter TargetName="Indicator" Property="Visibility" Value="Visible" />
                            <Setter Property="FontWeight" Value="SemiBold" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- 状态徽章 -->
    <Style x:Key="Ui.StatusBadge" TargetType="Border">
        <Setter Property="Height" Value="20" />
        <Setter Property="Padding" Value="8,0" />
        <Setter Property="CornerRadius" Value="{StaticResource Ui.Radius.Pill}" />
        <Setter Property="Background" Value="{DynamicResource Ui.Status.Neutral.Bg}" />
        <Setter Property="TextElement.Foreground" Value="{DynamicResource Ui.Status.Neutral.Fg}" />
        <Setter Property="TextElement.FontSize" Value="{StaticResource Ui.Type.Caption}" />
    </Style>
</ResourceDictionary>
```

### 8.6 外壳 XAML（配合 WPF UI 的 FluentWindow）

```xml
<ui:FluentWindow x:Class="App.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
    ExtendsContentIntoTitleBar="True" WindowBackdropType="None"
    Background="{DynamicResource Ui.Bg.Canvas}" MinWidth="800" MinHeight="500">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="48" />                   <!-- Ui.Size.TopBar -->
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="260" />                 <!-- Sidebar -->
            <ColumnDefinition Width="*" />                   <!-- Main island -->
            <ColumnDefinition Width="Auto" />                <!-- Side island（可选） -->
            <ColumnDefinition Width="48" />                  <!-- Rail -->
        </Grid.ColumnDefinitions>

        <!-- 顶栏：TitleBar 提供拖动区与系统按钮，搜索框叠放在其上方居中 -->
        <ui:TitleBar Grid.ColumnSpan="4" Title="应用名" />
        <ui:TextBox Grid.ColumnSpan="4" Width="480" Height="32"
                    HorizontalAlignment="Center" VerticalAlignment="Center"
                    PlaceholderText="搜索或跳转… (Ctrl+K)"
                    WindowChrome.IsHitTestVisibleInChrome="True" />

        <!-- 侧边导航 -->
        <DockPanel Grid.Row="1" Grid.Column="0">
            <StackPanel DockPanel.Dock="Bottom" Margin="0,0,0,8"><!-- 帮助 / 收起 --></StackPanel>
            <ListBox Background="Transparent" BorderThickness="0"
                     ItemContainerStyle="{StaticResource Ui.NavItem}" />
        </DockPanel>

        <!-- 主岛 -->
        <Border Grid.Row="1" Grid.Column="1" Style="{StaticResource Ui.Island}" Margin="0,0,0,8">
            <ScrollViewer VerticalScrollBarVisibility="Auto">
                <StackPanel MaxWidth="{StaticResource Ui.Content.MaxWidth.Workspace}"
                            Margin="{StaticResource Ui.Content.Padding.Workspace}">
                    <!-- 面包屑、标题行、页签、过滤栏、列表 -->
                </StackPanel>
            </ScrollViewer>
        </Border>

        <!-- 侧岛（可选，Visibility 绑定到 Rail 的选中状态） -->
        <Border Grid.Row="1" Grid.Column="2" Width="360" Margin="8,0,0,8"
                Style="{StaticResource Ui.Island}" Visibility="Collapsed" />

        <!-- 右侧工具条 -->
        <StackPanel Grid.Row="1" Grid.Column="3" HorizontalAlignment="Center">
            <!-- ui:Button Appearance="Transparent" Icon="{ui:SymbolIcon ...}" -->
        </StackPanel>
    </Grid>
</ui:FluentWindow>
```

> 主岛的右边距由 Rail 列提供；不使用 Rail 时把主岛 `Margin` 改为 `0,0,8,8`。如需 Mica 背景，可把 `WindowBackdropType` 设为 `Mica`，并把 `Ui.Bg.Canvas` 设为透明，让 Mica 充当海面。

### 8.7 圆角裁剪

WPF 的 `Border.CornerRadius` 不会裁剪子元素，岛或卡片中放入有背景的控件（`DataGrid`、`ListView`、图片）时角会溢出。使用附加属性按圆角生成裁剪区：

```csharp
public static class UiClip
{
    public static readonly DependencyProperty ToCornerRadiusProperty =
        DependencyProperty.RegisterAttached("ToCornerRadius", typeof(bool), typeof(UiClip),
            new PropertyMetadata(false, OnChanged));

    public static void SetToCornerRadius(Border b, bool v) => b.SetValue(ToCornerRadiusProperty, v);
    public static bool GetToCornerRadius(Border b) => (bool)b.GetValue(ToCornerRadiusProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.SizeChanged -= Update;
        if ((bool)e.NewValue) border.SizeChanged += Update;
        else if (border.Child is UIElement c) c.Clip = null;
    }

    private static void Update(object sender, SizeChangedEventArgs e)
    {
        var b = (Border)sender;
        if (b.Child is not FrameworkElement child) return;
        var r = Math.Max(0, b.CornerRadius.TopLeft - b.BorderThickness.Left);
        child.Clip = new RectangleGeometry(
            new Rect(0, 0, child.ActualWidth, child.ActualHeight), r, r);
    }
}
```

```xml
<Border Style="{StaticResource Ui.Island}" local:UiClip.ToCornerRadius="True">
    <DataGrid … />
</Border>
```

### 8.8 与 WPF UI 内置资源的对应

使用 WPF UI 时，可以直接让它的控件保持原样，本规范的画刷只用于外壳与自定义组件；或者在 `Light.xaml` / `Dark.xaml` 中用本规范的值覆盖同名键，让 WPF UI 控件也遵循本规范。常用对应关系（键名以所用 WPF UI 版本为准）：

| 本规范 | WPF UI 资源键 |
|---|---|
| `Ui.Bg.Canvas` | `ApplicationBackgroundBrush` |
| `Ui.Bg.Card` | `CardBackgroundFillColorDefaultBrush` |
| `Ui.Stroke.Card` | `CardStrokeColorDefaultBrush` |
| `Ui.Bg.Control` | `ControlFillColorDefaultBrush` |
| `Ui.Bg.Subtle.Hover` | `SubtleFillColorSecondaryBrush` |
| `Ui.Text.Primary` | `TextFillColorPrimaryBrush` |
| `Ui.Text.Secondary` | `TextFillColorSecondaryBrush` |
| `Ui.Text.Tertiary` | `TextFillColorTertiaryBrush` |
| `Ui.Text.Link` | `AccentTextFillColorPrimaryBrush` |

组件对应：SettingCard → `ui:CardControl`；ExpanderCard → `ui:CardExpander`；导航 → `ui:NavigationView`（需要岛屿式外壳时建议自绘侧栏，以便控制选中竖条与背景层级）；开关 → `ui:ToggleSwitch`；图标 → `ui:SymbolIcon`。

---

## 9. 评审清单

提交界面前逐项确认：

- [ ] 外壳为岛屿式：导航在 `bg.canvas`，内容在 `bg.island`，岛与窗口边缘有 8px 间隙
- [ ] 圆角按层级：岛 12 > 卡片 8 > 控件 4
- [ ] 同组卡片间距 4，区块间距 24–48，没有随意的间距值（全部是 4 的倍数）
- [ ] 内容列限宽（1000 / 1280），面包屑贴岛左上角
- [ ] 每个视图最多一个主按钮，全应用只用一种主按钮方案（A 或 B）
- [ ] 强调色只出现在选中、焦点、链接、主操作上
- [ ] 状态同时有图标形状和文字
- [ ] 页面只有一个 `type.title`，字号层级不超过三级
- [ ] 所有颜色都通过令牌引用（Web 用 `var()`，WPF 用 `DynamicResource`），没有硬编码色值
- [ ] 浅色、深色两种主题都检查过对比度
- [ ] 键盘可完成全部操作，焦点环清晰可见
- [ ] 减少动态效果设置下无动画
- [ ] 窄屏（< 640）下侧栏变抽屉，岛铺满
- [ ] 文案使用动词开头的按钮、句首大写、不使用全大写标签
- [ ] 空状态给出原因和可执行操作；错误信息说明问题与解决方法
