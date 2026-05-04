global using System;

/*
基于你提供的 `UTTDumper` 文档和 `uttdump_help.md` 教程，以下是针对 IDA Pro 的详细查找偏移（Offset/RVA）中文说明。

在使用 IDA 打开 `libunity.so` (Android) 或 `UnityPlayer.dll` (PC) 后，请按 `Shift + F12` 打开字符串窗口进行搜索，按 `X` 键查看引用。

---

### 1. version (Unity 版本) **直接填入字符串更快别找了**
*   **含义**: `GetUnityBuildFullVersion` 的地址或版本字符串地址。
*   **搜索字符串**: `Initialize engine version: %s\n`
*   **操作**:
    1. 搜索上述字符串并查看其交叉引用（XRef）。
    2. 在引用该字符串的代码上方，通常会调用一个函数来获取版本，或者直接引用了一个字符串（如 "2017.4.3f1"）。
    3. 记录该函数的 RVA 或字符串的 RVA。

### 2. common_strings (通用字符串) 需要的是指向字符串的指针 别搞错了
*   **含义**: `Unity::CommonString` 数组的起始地址。
*   **搜索字符串**: `AABB`
*   **操作**:
    1. 搜索字符串 `AABB` 如果找不到也可以找 `m_Script` 然后看上方是否有AABB。
    2. 只有几个结果，找到那个看起来像是字符串列表开头的地方（通常周围还有其他短字符串）。
    3. `AABB` 所在的地址即为 `common_strings` 的 RVA。

### 3. rtti (运行时类型信息)
*   **含义**: `RTTI::ms_runtimeTypes` 的地址。
*   **搜索字符串**: `Source and Destination Types do not match`
*   **操作**:
    1. 搜索上述字符串并查看引用。
    2. 在引用该字符串的代码位置 **之前**（上方），通常有一个指针或地址加载指令。
    3. 这个地址指向类型列表，即为 `rtti` 的 RVA（不需要减去指针大小）。

### 4. type_tree_ctor (TypeTree 构造函数)
*   **含义**: `TypeTree::TypeTree` 的地址。
*   **搜索字符串**: `Source and Destination Types do not match`
*   **操作**:
    1. 同样查看上述字符串的引用函数。
    2. **Unity 2019.4.x 及更新版本**: 找到引用点之后的 **第1个** 被调用的函数。
    3. **更早的版本**: 在该函数内，先找到第一个带有 `if` 判断的逻辑块，然后取该块之后的 **第1个** 被调用的函数。

### 5. type_tree (获取 TypeTree)
*   **含义**: `TypeTreeCache::GetTypeTree` (新版) 或 `GenerateTypeTree` (旧版)。
*   **搜索字符串**: `Source and Destination Types do not match`
*   **操作**:
    1. 同样查看上述字符串的引用函数。
    2. **Unity 2019.4.x 及更新版本**: 找到引用点之后的 **第2个** 被调用的函数。
    3. **更早的版本**: 在该函数内，先找到第一个带有 `if` 判断的逻辑块，然后取该块之后的 **第2个** 被调用的函数。

### 6. produce (对象生成)
*   **含义**: `Object::Produce` 的地址。
*   **搜索字符串**: `Failure to create component of type '%s' (0x%08X)`
*   **操作**:
    1. 搜索上述字符串并查看引用。
    2. 找到引用该字符串的函数，通常这就是 `Produce` 函数，或者是其顶部的第一个子函数调用。
    3. **特殊情况 (如 Unity 2022.3.24f1)**: 如果上面的搜不到，尝试搜索 `Could not produce class with ID %d.`，然后在 `if` 块之前的那个函数即是目标。

---

### 配置文件示例说明
在 `config.ini` 或类似配置中填入找到的 RVA（十六进制地址，例如 `0x123ABC`）。

*   **delay**: 0 (转储前的等待秒数)
*   **binary**: 目标文件名 (如 `libunity.so` 或 `UnityPlayer.dll`)
*   **output_dir**: 输出路径
*   **transfer**: 256 (一般默认即可，详见 transfer.h)
*   **version/common_strings/rtti...**: 填入你在 IDA 中找到的 RVA。

**提示**: RVA (Relative Virtual Address) = IDA 中的显示地址 - 基址 (Image Base)。如果 IDA 显示地址是 `0x180XXXXXX` 这种很大的数，记得减去基址（通常是 `0x180000000` 或 `0x10000000`，具体看 IDA 的 Segments 信息）。如果是 `libunity.so`，通常直接用 IDA 显示的偏移即可（基址通常为 0）。

*/