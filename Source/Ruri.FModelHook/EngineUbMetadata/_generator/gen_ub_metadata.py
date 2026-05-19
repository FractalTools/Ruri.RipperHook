"""EngineUbMetadata generator.

Walks UE engine source, finds every
``BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT[_WITH_CONSTRUCTOR]`` /
``BEGIN_UNIFORM_BUFFER_STRUCT[_WITH_CONSTRUCTOR]`` /
``BEGIN_SHADER_PARAMETER_STRUCT`` block, re-implements the layout +
``ComputeHash`` math from ``ShaderParameterMetadata.cpp`` +
``RHIUniformBufferLayoutInitializer.h``, and emits one
``<UBName>_<LayoutHash:08x>_MetaData.json`` per uniform buffer.

No C++ compiler. No AI lookup. Just regex + a tiny C-preprocessor for
``#define`` constants and "macro tables" (``VIEW_UNIFORM_BUFFER_MEMBER_TABLE``).

Usage::

    python gen_ub_metadata.py \
        --engine-src D:\\GameStudy\\UnrealEngine-5.1.1-release \
        --engine-version 5.1.1 \
        --out-dir D:\\Ruri\\...\\EngineUbMetadata \
        --target-folder GAME_UE5_1

    python gen_ub_metadata.py --engine-src ... --engine-version 5.4.4 \
        --out-dir ... --target-folder GAME_UE5_4

    # validate-only mode (compares against existing JSONs, no writes):
    python gen_ub_metadata.py --engine-src ... --validate ...\\GAME_UE5_1
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

# ---------------------------------------------------------------------------
# Constants from UE source: EUniformBufferBaseType + alignment + size table.
# Mirrors ShaderParameterMacros.h:780-1116 + ShaderParameterMetadata.cpp:471 +
# RHIUniformBufferLayoutInitializer.h:62-92. Byte-identical between UE 5.1.1
# and UE 5.4.4 (verified in UE_SYMBOL_SOURCES.md §3).
# ---------------------------------------------------------------------------

# EUniformBufferBaseType enum (RHIDefinitions.h:1414).
UBMT = {
    "INVALID":                       0,
    "BOOL":                          1,
    "INT32":                         2,
    "UINT32":                        3,
    "FLOAT32":                       4,
    "TEXTURE":                       5,
    "SRV":                           6,
    "UAV":                           7,
    "SAMPLER":                       8,
    "RDG_TEXTURE":                   9,
    "RDG_TEXTURE_ACCESS":           10,
    "RDG_TEXTURE_ACCESS_ARRAY":     11,
    "RDG_TEXTURE_SRV":              12,
    "RDG_TEXTURE_UAV":              13,
    "RDG_BUFFER_ACCESS":            14,
    "RDG_BUFFER_ACCESS_ARRAY":      15,
    "RDG_BUFFER_SRV":               16,
    "RDG_BUFFER_UAV":               17,
    "RDG_UNIFORM_BUFFER":           18,
    "NESTED_STRUCT":                19,
    "INCLUDED_STRUCT":              20,
    "REFERENCED_STRUCT":            21,
    "RENDER_TARGET_BINDING_SLOTS":  22,
}

POINTER_ALIGN = 8        # SHADER_PARAMETER_POINTER_ALIGNMENT
ARRAY_ELEM_ALIGN = 16    # SHADER_PARAMETER_ARRAY_ELEMENT_ALIGNMENT
STRUCT_ALIGN = 16        # SHADER_PARAMETER_STRUCT_ALIGNMENT

# Numeric type table: cpp_name -> (natural_size, alignment, UBMT, hlsl_name, RowCount, ColumnCount).
# ``natural_size`` is ``sizeof(T)`` — NOT padded to alignment. MS_ALIGN /
# GCC_ALIGN modifiers (used by ``TAlignedTypedef<T, A>::Type``) raise alignof
# but do NOT inflate sizeof; e.g. an alignas(16) FVector3f still has sizeof=12.
# The next member is aligned to its own type's alignment, so size and alignment
# matter independently — this matches what compilers actually do for cooked
# shipping UE structs (and what the layout hash discriminator XOR-folds).
#
# RowCount/ColumnCount convention follows the PROJECT'S `NumericShaderParameter`
# convention (see `MaterialConstantBufferReader.AddVectorMember` for the
# canonical pattern, and `LayoutBuilder.cs::TryCreateLogicalTypeFromMetadata`
# downstream which reads `RowCount==1` as "scalar" / `RowCount>1` as "vector
# with N components"):
#   * Scalar:   RowCount=1,  ColumnCount=1
#   * Vector N: RowCount=N,  ColumnCount=1   (N=2/3/4 — RowCount is the component count)
#   * Matrix:   RowCount=R,  ColumnCount=C   (paired with IsMatrix=True elsewhere)
# Using the HLSL row/col semantic instead (RowCount=1, ColumnCount=N for vec-N)
# silently collapses every vector field to a scalar in the rewriter.
TYPE_TABLE: dict[str, tuple[int, int, str, str, int, int]] = {
    "bool":          ( 4,  4, "BOOL",    "Bool",    1, 1),
    "uint32":        ( 4,  4, "UINT32",  "UInt",    1, 1),
    "int32":         ( 4,  4, "INT32",   "Int",     1, 1),
    "int":           ( 4,  4, "INT32",   "Int",     1, 1),
    "uint":          ( 4,  4, "UINT32",  "UInt",    1, 1),
    "float":         ( 4,  4, "FLOAT32", "Float",   1, 1),
    "FVector2f":     ( 8,  8, "FLOAT32", "Float2",  2, 1),
    "FVector3f":     (12, 16, "FLOAT32", "Float3",  3, 1),
    "FVector4f":     (16, 16, "FLOAT32", "Float4",  4, 1),
    "FLinearColor":  (16, 16, "FLOAT32", "Float4",  4, 1),
    "FIntPoint":     ( 8,  8, "INT32",   "Int2",    2, 1),
    "FUintVector2":  ( 8,  8, "UINT32",  "UInt2",   2, 1),
    "FIntVector":    (12, 16, "INT32",   "Int3",    3, 1),
    "FUintVector3":  (12, 16, "UINT32",  "UInt3",   3, 1),
    "FIntVector4":   (16, 16, "INT32",   "Int4",    4, 1),
    "FUintVector4":  (16, 16, "UINT32",  "UInt4",   4, 1),
    "FIntRect":      (16, 16, "INT32",   "Int4",    4, 1),
    "FQuat4f":       (16, 16, "FLOAT32", "Float4",  4, 1),
    "FMatrix44f":    (64, 16, "FLOAT32", "Float4x4", 4, 4),
    "FMatrix3x4f":   (48, 16, "FLOAT32", "Float3x4", 3, 4),
    "FMatrix44d":    (64, 16, "FLOAT32", "Float4x4", 4, 4),  # treated like 44f for layout
    # LWC types -- same shape as their float counterparts post-cooking.
    "FVector":       (12, 16, "FLOAT32", "Float3",  3, 1),
    "FVector4":      (16, 16, "FLOAT32", "Float4",  4, 1),
    "FMatrix":       (64, 16, "FLOAT32", "Float4x4", 4, 4),
    "FMatrix3x4":    (48, 16, "FLOAT32", "Float3x4", 3, 4),  # LWC alias of FMatrix3x4f
    "FMatrix44":     (64, 16, "FLOAT32", "Float4x4", 4, 4),  # LWC alias of FMatrix44f (rarely used)
}

# Scalar packed-array helper. SHADER_PARAMETER_SCALAR_ARRAY(T, name, [N])
# expands to SHADER_PARAMETER_ARRAY(<PackedType>, name, [(N+3)/4]). Keep the
# packed-type table in sync with TShaderParameterScalarArrayTypeInfo
# (ShaderParameterMacros.h ~1900-1910).
SCALAR_ARRAY_PACK = {
    "uint32": "FUintVector4",
    "uint":   "FUintVector4",
    "int32":  "FIntVector4",
    "int":    "FIntVector4",
    "float":  "FVector4f",
}

# Resource-pointer pseudo-type (used by SHADER_PARAMETER_TEXTURE / SRV / UAV /
# SAMPLER / RDG_*). sizeof = 8 (pointer), alignment = 8.
RESOURCE_INFO = (POINTER_ALIGN, POINTER_ALIGN)


# ---------------------------------------------------------------------------
# AST
# ---------------------------------------------------------------------------

@dataclass
class Member:
    """One macro line inside a BEGIN_...STRUCT block, post-table-expansion."""
    macro: str           # e.g. SHADER_PARAMETER, SHADER_PARAMETER_TEXTURE, ...
    cpp_type: str        # e.g. FMatrix44f, FViewUniformShaderParameters, Texture2D
    name: str
    array_decl: str = ""  # e.g. "[4]" or "[TVC_MAX]" — empty for non-array
    is_struct_include: bool = False
    is_struct_nested: bool = False
    is_resource: bool = False
    ubmt: str = ""       # UBMT_* enum NAME (no prefix)
    src_file: str = ""
    src_line: int = 0


@dataclass
class StructDef:
    """Parsed BEGIN_..._STRUCT body, before layout/hash computation."""
    cpp_name: str                # e.g. FViewUniformShaderParameters
    kind: str                    # "ub" (UNIFORM_BUFFER / GLOBAL_SHADER_PARAMETER) | "param" (SHADER_PARAMETER_STRUCT)
    members: list[Member]
    src_file: str
    src_line: int


@dataclass
class Resource:
    """One entry that will land in FRHIUniformBufferLayoutInitializer.Resources[]."""
    name: str
    ubmt_name: str   # without UBMT_ prefix
    ubmt_value: int
    offset: int      # the post-walked AbsoluteMemberOffset, hash input
    resource_index: int  # position in Resources[] after offset-sort, JSON `index` field


@dataclass
class NumericMember:
    name: str
    offset: int      # struct offset in bytes
    hlsl_type: str   # "Float4x4", "Int3", ... matches loader ParseType
    array_size: int  # 0 if not an array
    num_rows: int
    num_columns: int


@dataclass
class LayoutResult:
    cpp_name: str
    numeric: list[NumericMember]
    resources: list[Resource]   # sorted by offset
    struct_size: int             # sizeof(C++ struct) — feeds the hash
    cb_size: int                 # numeric-only size (for JSON.constantBufferSize)
    layout_hash: int


# ---------------------------------------------------------------------------
# Tiny C-preprocessor: collect #define constants + macro "tables".
# ---------------------------------------------------------------------------

_CONST_RE = re.compile(
    r"^[ \t]*#define[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]+(\(?[-0-9xXa-fA-F]+\)?)[ \t]*(?://.*)?$",
    re.MULTILINE,
)

_ENUM_RE = re.compile(
    r"enum(?:[ \t]+class)?[ \t]+([A-Za-z_][A-Za-z0-9_]*)\b[^{]*\{([^}]*)\}",
    re.MULTILINE | re.DOTALL,
)

_NAMESPACE_INT_RE = re.compile(
    r"(?:static[ \t]+)?(?:constexpr[ \t]+|const[ \t]+)+(?:int(?:32)?|uint32|size_t)[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*([-+0-9xXa-fA-F]+)",
    re.MULTILINE,
)


def collect_constants(engine_src: Path) -> dict[str, int]:
    """Sweep engine source for ``#define NAME LITERAL_INT`` and a handful of
    ``constexpr int NAME = N;`` declarations that array sizes depend on.

    Keys are returned bare (``TVC_MAX``) **and** namespace-qualified
    (``GlobalDistanceField::MaxClipmaps``) so that array-decls hit either form.
    """
    out: dict[str, int] = {}
    for fp in iter_cpp_files(engine_src):
        try:
            text = read_text(fp)
        except OSError:
            continue
        for m in _CONST_RE.finditer(text):
            name = m.group(1)
            raw = m.group(2).strip("()")
            try:
                val = int(raw, 0)
            except ValueError:
                continue
            out.setdefault(name, val)
        # enums: walk members, auto-assign incrementing values.
        for em in _ENUM_RE.finditer(text):
            body = em.group(2)
            cur = 0
            for raw_member in body.split(","):
                raw_member = raw_member.strip()
                if not raw_member:
                    continue
                # strip a trailing comment
                raw_member = re.sub(r"//.*$", "", raw_member).strip()
                if "=" in raw_member:
                    name, _, val = raw_member.partition("=")
                    name = name.strip()
                    try:
                        cur = int(val.strip(), 0)
                    except ValueError:
                        pass
                else:
                    name = raw_member
                if name:
                    out.setdefault(name, cur)
                    cur += 1
        # namespace-qualified constexpr ints (e.g. GlobalDistanceField::MaxClipmaps).
        ns_stack: list[str] = []
        for line in text.splitlines():
            ns_match = re.match(r"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{?", line)
            if ns_match:
                ns_stack.append(ns_match.group(1))
                continue
            if re.match(r"^\s*\}\s*$", line) and ns_stack:
                ns_stack.pop()
                continue
            mm = _NAMESPACE_INT_RE.search(line)
            if mm:
                name, raw = mm.group(1), mm.group(2)
                try:
                    val = int(raw, 0)
                except ValueError:
                    continue
                out.setdefault(name, val)
                if ns_stack:
                    qualified = "::".join(ns_stack + [name])
                    out.setdefault(qualified, val)
    return out


# Engine-side typedefs / aliases we resolve manually. Add more as needed.
# (UE 5.1+ aliases FMatrix44f, FVector4f, etc. directly so these are mostly
# legacy unaliased names.)
TYPE_ALIASES = {
    "FShaderResourceViewRHIRef":   "FRHIShaderResourceView*",
    "FUnorderedAccessViewRHIRef":  "FRHIUnorderedAccessView*",
}


def collect_macro_tables(engine_src: Path) -> dict[str, str]:
    """Returns ``{macro_name: expansion_body}`` for every ``#define`` whose
    body (after collapsing ``\\``-continuations) contains ``SHADER_PARAMETER``
    or another known table identifier — these are the "macro tables" UE uses
    to share large parameter blocks (``VIEW_UNIFORM_BUFFER_MEMBER_TABLE``,
    ``DECLARE_LUMEN_RADIANCE_CACHE_PARAMETERS``, …).

    Two-pass approach: collapse line continuations first, then a simple
    ``#define NAME[(args)] body`` regex. Avoids the brittle one-shot regex
    that drops bodies whose first line ends in ``\\`` mid-token.
    """
    tables: dict[str, str] = {}
    for fp in iter_cpp_files(engine_src):
        try:
            text = read_text(fp)
        except OSError:
            continue
        # Quick reject: only scan files mentioning at least one SHADER_PARAMETER.
        if "SHADER_PARAMETER" not in text and "_UNIFORM_BUFFER_MEMBER" not in text:
            continue
        # Collapse backslash-newline continuations.
        joined = re.sub(r"\\\r?\n", " ", text)
        for m in re.finditer(
            r"^[ \t]*#define[ \t]+([A-Za-z_][A-Za-z0-9_]*)(\([^)]*\))?[ \t]+([^\n]+)",
            joined, re.MULTILINE,
        ):
            name = m.group(1)
            params = m.group(2) or ""
            body = m.group(3).strip()
            if "SHADER_PARAMETER" not in body and "_UNIFORM_BUFFER_MEMBER" not in body:
                continue
            # Skip the foundational language macros defined in
            # ShaderParameterMacros.h — they define what SHADER_PARAMETER /
            # BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT mean. Expanding them would
            # explode every member declaration into INTERNAL_SHADER_PARAMETER_EXPLICIT(...)
            # gibberish and prevent us from re-parsing the block.
            if (name.startswith("SHADER_PARAMETER")
                or name.startswith("BEGIN_") or name.startswith("END_")
                or name.startswith("INTERNAL_") or name.startswith("IMPLEMENT_")
                or name.startswith("RENDER_TARGET_")
                or name.startswith("RDG_")):
                continue
            tables.setdefault(name, body)
            if params:
                # Remember its parameter list so expansion can do positional substitution.
                # Encode as a sentinel-prefixed key so we can recover both.
                tables.setdefault(f"__params__::{name}", params)
    return tables


def expand_macro_tables(text: str, tables: dict[str, str], depth: int = 0) -> str:
    """Recursively substitute macro-table identifiers with their expansion.

    Handles both object-like ``#define NAME body`` and function-like
    ``#define NAME(arg1, arg2) body`` definitions; the latter pulls the
    parameter list from the sibling ``__params__::NAME`` key.

    Caps recursion at 6 to absorb the typical
    ``VIEW_UNIFORM_BUFFER_MEMBER_TABLE`` -> ``VIEW_UNIFORM_BUFFER_MEMBER``
    -> ``SHADER_PARAMETER`` chain without infinite-looping on a cycle.
    """
    if depth > 6:
        return text
    changed = False
    out = text
    # Build expansion order: function-like first (more specific), then object-like.
    fn_names = []
    obj_names = []
    for name in tables.keys():
        if name.startswith("__params__::"):
            continue
        if f"__params__::{name}" in tables:
            fn_names.append(name)
        else:
            obj_names.append(name)
    # Function-like expansion
    for name in fn_names:
        if name not in out:
            continue
        body = tables[name]
        params_raw = tables[f"__params__::{name}"]
        # parse param names out of "(a, b, c)"
        params = [p.strip() for p in params_raw.strip("()").split(",") if p.strip()]
        mre = re.compile(r"\b" + re.escape(name) + r"\s*\(([^()]*)\)")
        def repl(match: re.Match[str], _body=body, _params=params) -> str:
            args = [a.strip() for a in split_top_commas(match.group(1))]
            expanded = _body
            # Substitute each formal param with its actual value (whole-word).
            for i, p in enumerate(_params):
                if i < len(args):
                    expanded = re.sub(r"\b" + re.escape(p) + r"\b", args[i], expanded)
            return expanded
        new_out, n = mre.subn(repl, out)
        if n > 0:
            out = new_out
            changed = True
    # Object-like expansion (whole-identifier match, no following parens).
    for name in obj_names:
        if name not in out:
            continue
        body = tables[name]
        pattern = re.compile(r"\b" + re.escape(name) + r"\b(?!\s*\()")
        new_out, n = pattern.subn(body, out)
        if n > 0:
            out = new_out
            changed = True
    if changed:
        return expand_macro_tables(out, tables, depth + 1)
    return out


def split_top_commas(s: str) -> list[str]:
    """Split on commas not inside [] / () / <> brackets."""
    out, buf, depth = [], [], 0
    for ch in s:
        if ch in "([<{":
            depth += 1
        elif ch in ")]>}":
            depth -= 1
        if ch == "," and depth == 0:
            out.append("".join(buf))
            buf = []
        else:
            buf.append(ch)
    out.append("".join(buf))
    return out


# ---------------------------------------------------------------------------
# File walk + text extraction
# ---------------------------------------------------------------------------

CPP_EXTS = {".h", ".hpp", ".inl", ".cpp", ".c"}


def iter_cpp_files(engine_src: Path):
    """Yield every C++ source under ``Engine/Source/`` (and ``Engine/Plugins``
    for completeness — many UB layouts live in plugins, e.g. Niagara)."""
    roots = [
        engine_src / "Engine" / "Source" / "Runtime",
        engine_src / "Engine" / "Source" / "Developer",
        engine_src / "Engine" / "Source" / "Editor",
        engine_src / "Engine" / "Plugins",
    ]
    for root in roots:
        if not root.exists():
            continue
        for dp, _dn, fn in os.walk(root):
            for f in fn:
                if Path(f).suffix.lower() in CPP_EXTS:
                    yield Path(dp) / f


_text_cache: dict[Path, str] = {}


def read_text(fp: Path) -> str:
    if fp in _text_cache:
        return _text_cache[fp]
    data = fp.read_bytes()
    # UTF-8 with BOM tolerance; fall back to latin-1 (UE source is ASCII).
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = data.decode("latin-1")
    _text_cache[fp] = text
    return text


# ---------------------------------------------------------------------------
# Locating and parsing BEGIN_..._STRUCT blocks
# ---------------------------------------------------------------------------

_BEGIN_UB_RE = re.compile(
    r"BEGIN_(?:GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT)"
    r"(?:_WITH_CONSTRUCTOR)?\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,",
)
_BEGIN_PARAM_RE = re.compile(
    r"BEGIN_SHADER_PARAMETER_STRUCT\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,",
)
_END_BLOCK_RE = re.compile(
    r"END_(?:GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT|SHADER_PARAMETER_STRUCT)\s*\(\s*\)",
)


def find_struct_blocks(text: str) -> list[tuple[int, int, str, str]]:
    """Return ``[(span_start, span_end, struct_name, kind), ...]`` for every
    BEGIN_..._STRUCT block in ``text``. ``kind`` is "ub" or "param"."""
    out: list[tuple[int, int, str, str]] = []
    for m in _BEGIN_UB_RE.finditer(text):
        # find the matching END_..._STRUCT()
        end = _END_BLOCK_RE.search(text, m.end())
        if end is None:
            continue
        out.append((m.start(), end.end(), m.group(1), "ub"))
    for m in _BEGIN_PARAM_RE.finditer(text):
        end = _END_BLOCK_RE.search(text, m.end())
        if end is None:
            continue
        out.append((m.start(), end.end(), m.group(1), "param"))
    return out


# Member macros we recognise. Each line: one regex matching "MACRO(args)".
# We match greedily to one top-level call by counting parens.
_MEMBER_MACROS = (
    # constant-buffer numeric
    "SHADER_PARAMETER", "SHADER_PARAMETER_EX",
    "SHADER_PARAMETER_ARRAY", "SHADER_PARAMETER_ARRAY_EX",
    "SHADER_PARAMETER_SCALAR_ARRAY",
    # resources
    "SHADER_PARAMETER_TEXTURE", "SHADER_PARAMETER_TEXTURE_ARRAY",
    "SHADER_PARAMETER_SRV", "SHADER_PARAMETER_SRV_ARRAY",
    "SHADER_PARAMETER_UAV", "SHADER_PARAMETER_UAV_ARRAY",
    "SHADER_PARAMETER_SAMPLER", "SHADER_PARAMETER_SAMPLER_ARRAY",
    "SHADER_PARAMETER_RDG_TEXTURE", "SHADER_PARAMETER_RDG_TEXTURE_ARRAY",
    "SHADER_PARAMETER_RDG_TEXTURE_SRV", "SHADER_PARAMETER_RDG_TEXTURE_SRV_ARRAY",
    "SHADER_PARAMETER_RDG_TEXTURE_UAV", "SHADER_PARAMETER_RDG_TEXTURE_UAV_ARRAY",
    "SHADER_PARAMETER_RDG_TEXTURE_NON_PIXEL_SRV",
    "SHADER_PARAMETER_RDG_BUFFER_SRV", "SHADER_PARAMETER_RDG_BUFFER_SRV_ARRAY",
    "SHADER_PARAMETER_RDG_BUFFER_UAV", "SHADER_PARAMETER_RDG_BUFFER_UAV_ARRAY",
    "SHADER_PARAMETER_RDG_UNIFORM_BUFFER",
    # struct nesting/inclusion
    "SHADER_PARAMETER_STRUCT", "SHADER_PARAMETER_STRUCT_INCLUDE",
    "SHADER_PARAMETER_STRUCT_REF", "SHADER_PARAMETER_STRUCT_ARRAY",
    # RDG access types (ShaderParameterStruct only — not in UBs but parse robustness)
    "RDG_BUFFER_ACCESS", "RDG_BUFFER_ACCESS_DYNAMIC", "RDG_BUFFER_ACCESS_ARRAY",
    "RDG_TEXTURE_ACCESS", "RDG_TEXTURE_ACCESS_DYNAMIC", "RDG_TEXTURE_ACCESS_ARRAY",
    "RENDER_TARGET_BINDING_SLOTS",
)

_MEMBER_OPENER_RE = re.compile(
    r"\b(" + "|".join(re.escape(m) for m in _MEMBER_MACROS) + r")\s*\(",
)


def parse_macro_call(text: str, opener_match: re.Match[str]) -> tuple[str, list[str], int]:
    """Given a ``MACRO(`` match, returns ``(macro_name, [args], end_pos)`` where
    ``end_pos`` is the index just past the closing ``)``."""
    macro = opener_match.group(1)
    start = opener_match.end()  # right after the '('
    depth = 1
    i = start
    while i < len(text) and depth > 0:
        ch = text[i]
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                break
        i += 1
    args = split_top_commas(text[start:i])
    args = [a.strip() for a in args]
    return macro, args, i + 1


# Map each macro to (is_resource, ubmt_no_prefix). Numeric/struct members map
# to a non-resource UBMT slot (FLOAT32 / NESTED_STRUCT / INCLUDED_STRUCT) since
# the actual base type for numeric is determined from the C++ type.
MACRO_INFO: dict[str, tuple[bool, str]] = {
    "SHADER_PARAMETER":                            (False, ""),
    "SHADER_PARAMETER_EX":                         (False, ""),
    "SHADER_PARAMETER_ARRAY":                      (False, ""),
    "SHADER_PARAMETER_ARRAY_EX":                   (False, ""),
    "SHADER_PARAMETER_SCALAR_ARRAY":               (False, ""),
    "SHADER_PARAMETER_TEXTURE":                    (True,  "TEXTURE"),
    "SHADER_PARAMETER_TEXTURE_ARRAY":              (True,  "TEXTURE"),
    "SHADER_PARAMETER_SRV":                        (True,  "SRV"),
    "SHADER_PARAMETER_SRV_ARRAY":                  (True,  "SRV"),
    "SHADER_PARAMETER_UAV":                        (True,  "UAV"),
    "SHADER_PARAMETER_UAV_ARRAY":                  (True,  "UAV"),
    "SHADER_PARAMETER_SAMPLER":                    (True,  "SAMPLER"),
    "SHADER_PARAMETER_SAMPLER_ARRAY":              (True,  "SAMPLER"),
    "SHADER_PARAMETER_RDG_TEXTURE":                (True,  "RDG_TEXTURE"),
    "SHADER_PARAMETER_RDG_TEXTURE_ARRAY":          (True,  "RDG_TEXTURE"),
    "SHADER_PARAMETER_RDG_TEXTURE_SRV":            (True,  "RDG_TEXTURE_SRV"),
    "SHADER_PARAMETER_RDG_TEXTURE_SRV_ARRAY":      (True,  "RDG_TEXTURE_SRV"),
    "SHADER_PARAMETER_RDG_TEXTURE_NON_PIXEL_SRV":  (True,  "RDG_TEXTURE_SRV"),
    "SHADER_PARAMETER_RDG_TEXTURE_UAV":            (True,  "RDG_TEXTURE_UAV"),
    "SHADER_PARAMETER_RDG_TEXTURE_UAV_ARRAY":      (True,  "RDG_TEXTURE_UAV"),
    "SHADER_PARAMETER_RDG_BUFFER_SRV":             (True,  "RDG_BUFFER_SRV"),
    "SHADER_PARAMETER_RDG_BUFFER_SRV_ARRAY":       (True,  "RDG_BUFFER_SRV"),
    "SHADER_PARAMETER_RDG_BUFFER_UAV":             (True,  "RDG_BUFFER_UAV"),
    "SHADER_PARAMETER_RDG_BUFFER_UAV_ARRAY":       (True,  "RDG_BUFFER_UAV"),
    "SHADER_PARAMETER_RDG_UNIFORM_BUFFER":         (True,  "RDG_UNIFORM_BUFFER"),
    "SHADER_PARAMETER_STRUCT":                     (False, "NESTED_STRUCT"),
    "SHADER_PARAMETER_STRUCT_INCLUDE":             (False, "INCLUDED_STRUCT"),
    "SHADER_PARAMETER_STRUCT_REF":                 (True,  "REFERENCED_STRUCT"),
    "SHADER_PARAMETER_STRUCT_ARRAY":               (False, "NESTED_STRUCT"),
    "RDG_BUFFER_ACCESS":                           (True,  "RDG_BUFFER_ACCESS"),
    "RDG_BUFFER_ACCESS_DYNAMIC":                   (True,  "RDG_BUFFER_ACCESS"),
    "RDG_BUFFER_ACCESS_ARRAY":                     (True,  "RDG_BUFFER_ACCESS_ARRAY"),
    "RDG_TEXTURE_ACCESS":                          (True,  "RDG_TEXTURE_ACCESS"),
    "RDG_TEXTURE_ACCESS_DYNAMIC":                  (True,  "RDG_TEXTURE_ACCESS"),
    "RDG_TEXTURE_ACCESS_ARRAY":                    (True,  "RDG_TEXTURE_ACCESS_ARRAY"),
    "RENDER_TARGET_BINDING_SLOTS":                 (True,  "RENDER_TARGET_BINDING_SLOTS"),
}


def parse_struct_block(
    text: str,
    span_start: int,
    span_end: int,
    cpp_name: str,
    kind: str,
    src_file: str,
    line_offset_table: list[int],
    macro_tables: dict[str, str],
) -> StructDef:
    body = text[span_start:span_end]
    # First, expand macro tables that appear within (e.g. VIEW_UNIFORM_BUFFER_MEMBER_TABLE).
    body = expand_macro_tables(body, macro_tables)

    members: list[Member] = []
    pos = 0
    while True:
        m = _MEMBER_OPENER_RE.search(body, pos)
        if m is None:
            break
        macro, args, end = parse_macro_call(body, m)
        pos = end
        info = MACRO_INFO.get(macro)
        if info is None:
            continue
        is_resource, ubmt = info

        # Decode args per macro shape.
        cpp_type = ""
        name = ""
        array_decl = ""
        is_include = (macro == "SHADER_PARAMETER_STRUCT_INCLUDE")
        is_nested = (macro in ("SHADER_PARAMETER_STRUCT", "SHADER_PARAMETER_STRUCT_ARRAY"))

        if macro in ("SHADER_PARAMETER", "SHADER_PARAMETER_EX"):
            if len(args) >= 2:
                cpp_type = args[0]
                name = args[1]
        elif macro in ("SHADER_PARAMETER_ARRAY", "SHADER_PARAMETER_ARRAY_EX", "SHADER_PARAMETER_SCALAR_ARRAY"):
            if len(args) >= 3:
                cpp_type = args[0]
                name = args[1]
                array_decl = args[2]
        elif macro in (
            "SHADER_PARAMETER_TEXTURE", "SHADER_PARAMETER_SRV", "SHADER_PARAMETER_UAV",
            "SHADER_PARAMETER_SAMPLER", "SHADER_PARAMETER_RDG_TEXTURE",
            "SHADER_PARAMETER_RDG_TEXTURE_SRV", "SHADER_PARAMETER_RDG_TEXTURE_NON_PIXEL_SRV",
            "SHADER_PARAMETER_RDG_TEXTURE_UAV", "SHADER_PARAMETER_RDG_BUFFER_SRV",
            "SHADER_PARAMETER_RDG_BUFFER_UAV", "SHADER_PARAMETER_RDG_UNIFORM_BUFFER",
            "SHADER_PARAMETER_STRUCT_REF",
        ):
            if len(args) >= 2:
                cpp_type = args[0]
                name = args[1]
        elif macro in (
            "SHADER_PARAMETER_TEXTURE_ARRAY", "SHADER_PARAMETER_SRV_ARRAY",
            "SHADER_PARAMETER_UAV_ARRAY", "SHADER_PARAMETER_SAMPLER_ARRAY",
            "SHADER_PARAMETER_RDG_TEXTURE_ARRAY", "SHADER_PARAMETER_RDG_TEXTURE_SRV_ARRAY",
            "SHADER_PARAMETER_RDG_TEXTURE_UAV_ARRAY", "SHADER_PARAMETER_RDG_BUFFER_SRV_ARRAY",
            "SHADER_PARAMETER_RDG_BUFFER_UAV_ARRAY",
        ):
            if len(args) >= 3:
                cpp_type = args[0]
                name = args[1]
                array_decl = args[2]
        elif macro in ("SHADER_PARAMETER_STRUCT", "SHADER_PARAMETER_STRUCT_INCLUDE"):
            if len(args) >= 2:
                cpp_type = args[0]
                name = args[1]
        elif macro == "SHADER_PARAMETER_STRUCT_ARRAY":
            if len(args) >= 3:
                cpp_type = args[0]
                name = args[1]
                array_decl = args[2]
        elif macro == "RENDER_TARGET_BINDING_SLOTS":
            cpp_type = "FRenderTargetBindingSlots"
            name = "RenderTargets"
        elif macro in ("RDG_BUFFER_ACCESS", "RDG_TEXTURE_ACCESS"):
            if len(args) >= 1:
                name = args[0]
        elif macro in ("RDG_BUFFER_ACCESS_DYNAMIC", "RDG_TEXTURE_ACCESS_DYNAMIC",
                       "RDG_BUFFER_ACCESS_ARRAY", "RDG_TEXTURE_ACCESS_ARRAY"):
            if len(args) >= 1:
                name = args[0]
        if not name:
            continue

        # Resolve typedefs
        cpp_type = TYPE_ALIASES.get(cpp_type, cpp_type)
        # Strip namespace qualifier on struct-include / struct-nested types.
        # UE source uses e.g. `SHADER_PARAMETER_STRUCT_INCLUDE(LumenRadianceCache::
        # FRadianceCacheInterpolationParameters, ...)` but `structs_by_name` is
        # keyed on the bare struct name. Without stripping, the lookup misses
        # and the whole nested branch is silently skipped — losing every
        # numeric field inside it.
        if (is_include or is_nested) and "::" in cpp_type:
            cpp_type = cpp_type.rsplit("::", 1)[-1]

        # locate file line of the macro
        abs_pos = span_start + m.start()
        src_line = bisect_line(line_offset_table, abs_pos)

        members.append(Member(
            macro=macro, cpp_type=cpp_type, name=name, array_decl=array_decl,
            is_struct_include=is_include, is_struct_nested=is_nested,
            is_resource=is_resource, ubmt=ubmt, src_file=src_file, src_line=src_line,
        ))
    line_no = bisect_line(line_offset_table, span_start)
    return StructDef(cpp_name=cpp_name, kind=kind, members=members, src_file=src_file, src_line=line_no)


def bisect_line(offsets: list[int], pos: int) -> int:
    """1-based line number for a byte position, given a precomputed table of
    line-start offsets."""
    import bisect
    return bisect.bisect_right(offsets, pos)


def build_line_table(text: str) -> list[int]:
    offsets = [0]
    for i, ch in enumerate(text):
        if ch == "\n":
            offsets.append(i + 1)
    return offsets


# ---------------------------------------------------------------------------
# UB name mapping (struct -> shader binding name)
# ---------------------------------------------------------------------------

# Each IMPLEMENT_*_STRUCT macro picks a specific EUniformBufferBindingFlags
# value when it constructs the FShaderParametersMetadata at static-init time.
# The flag bits are what `FRHIUniformBufferLayoutInitializer::ComputeHash`
# folds into bit 8 of the hash, so they MUST be correct or the hash drifts
# even when the layout is right.
#
# Source (5.1 + 5.4 identical):
#   ShaderParameterMacros.h:1376-1462 + RHIDefinitions.h:1464-1480.
#   EUniformBufferBindingFlags { Shader = 1, Static = 2, StaticAndShader = 3 }.
IMPLEMENT_BINDING_FLAGS: dict[str, tuple[int, bool]] = {
    # macro_name -> (binding_flags, has_static_slot)
    "IMPLEMENT_UNIFORM_BUFFER_STRUCT":                          (1, False),
    "IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT":                 (1, False),
    "IMPLEMENT_UNIFORM_BUFFER_ALIAS_STRUCT":                    (1, False),
    "IMPLEMENT_GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT":           (1, False),
    "IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT":                   (2, True),
    "IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX":                (2, True),
    "IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX2":               (2, True),
    "IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT":        (3, True),
    "IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT_EX":     (3, True),
}

_IMPLEMENT_RE = re.compile(
    r"\b(IMPLEMENT_(?:GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT|"
    r"STATIC_UNIFORM_BUFFER_STRUCT_EX2|"
    r"STATIC_UNIFORM_BUFFER_STRUCT_EX|"
    r"STATIC_UNIFORM_BUFFER_STRUCT|"
    r"STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT_EX|"
    r"STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT|"
    r"GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT|UNIFORM_BUFFER_ALIAS_STRUCT))"
    r"\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*\"([^\"]+)\"",
)


def collect_ub_name_map(engine_src: Path) -> dict[str, tuple[str, str, str, int, bool]]:
    """Returns ``{cpp_struct_name: (shader_var_name, static_slot_or_empty, file, binding_flags, has_static_slot)}``."""
    out: dict[str, tuple[str, str, str, int, bool]] = {}
    for fp in iter_cpp_files(engine_src):
        try:
            text = read_text(fp)
        except OSError:
            continue
        if "IMPLEMENT_" not in text:
            continue
        for m in _IMPLEMENT_RE.finditer(text):
            macro = m.group(1)
            cpp = m.group(2)
            shader = m.group(3)
            tail = text[m.end(): m.end() + 200]
            slot_m = re.match(r"\s*,\s*([A-Za-z_][A-Za-z0-9_]*)", tail)
            slot = slot_m.group(1) if slot_m else ""
            flags, has_slot = IMPLEMENT_BINDING_FLAGS.get(macro, (1, False))
            # Discard non-static IMPLEMENT macros that captured a 3rd identifier
            # by accident (e.g. trailing semicolon parse).
            if not has_slot:
                slot = ""
            out.setdefault(cpp, (shader, slot, str(fp), flags, has_slot))
    return out


# ---------------------------------------------------------------------------
# Layout pass: walks members and assigns offsets, computes resources/hash.
# Mirrors FShaderParametersMetadata::InitializeLayout (ShaderParameterMetadata.cpp:420)
# + FRHIUniformBufferLayoutInitializer::ComputeHash (RHIResources.h:806-836).
# ---------------------------------------------------------------------------

def evaluate_dim(expr: str, constants: dict[str, int]) -> int:
    """Evaluate an array-decl expression like ``[TVC_MAX]``, ``[NUM_X * 2]``,
    or ``[FCustomPrimitiveData::NumCustomPrimitiveDataFloat4s]``.

    Returns the number of elements. Falls back to 1 if unresolvable -- caller
    flags this as an unresolved dim, which beats silently emitting 0.
    """
    s = expr.strip().strip("[]").strip()
    if not s:
        return 0

    def repl_ident(m: re.Match[str]) -> str:
        ident = m.group(0)
        if ident in constants:
            return str(constants[ident])
        # Strip a leading ``Namespace::`` / ``Class::`` qualifier and retry --
        # ``collect_constants`` tracks ``namespace`` scopes but not
        # ``class``/``struct`` scopes, so the bare suffix is the more reliable
        # key. (E.g. ``FCustomPrimitiveData::NumCustomPrimitiveDataFloat4s``
        # resolves via the bare ``NumCustomPrimitiveDataFloat4s`` constant.)
        tail = ident.split("::")[-1]
        if tail in constants:
            return str(constants[tail])
        return ident

    s2 = re.sub(r"[A-Za-z_][A-Za-z0-9_:]*", repl_ident, s)
    if not re.match(r"^[\d+\-*/()\s]+$", s2):
        return 1
    try:
        return int(eval(s2, {"__builtins__": {}}, {}))  # noqa: S307 -- bounded literal eval
    except Exception:
        return 1


@dataclass
class _Cursor:
    """Mutable state while walking a struct (lives inside compute_layout)."""
    next_offset: int = 0
    resources: list[Resource] = field(default_factory=list)
    numeric: list[NumericMember] = field(default_factory=list)
    max_numeric_end: int = 0   # for the JSON `constantBufferSize` field


def align_up(v: int, a: int) -> int:
    return (v + a - 1) & ~(a - 1)


def compute_layout(
    sd: StructDef,
    structs_by_name: dict[str, StructDef],
    constants: dict[str, int],
    warn: callable,
) -> LayoutResult | None:
    """Walks ``sd``'s members, expanding included/nested structs, and computes
    the layout + hash. Returns ``None`` if a fatal type/struct cannot be
    resolved (skipped silently — the user can hand-author those JSONs)."""
    cur = _Cursor()

    def walk(s: StructDef, base_offset: int, prefix: str) -> None:
        # Each BEGIN_..._STRUCT body lays out members sequentially via builder
        # rules (align next to member's alignment, advance by sizeof(TAlignedType)).
        nonlocal cur
        local_next = 0  # offset within this nested struct
        for m in s.members:
            if m.macro == "RENDER_TARGET_BINDING_SLOTS":
                # ShaderParameterStruct only; skip silently in UBs.
                continue
            elif m.is_struct_include or m.is_struct_nested:
                if m.cpp_type not in structs_by_name:
                    warn(f"  ! {s.cpp_name}.{m.name}: nested/included struct '{m.cpp_type}' not found -- skipping")
                    continue
                child = structs_by_name[m.cpp_type]
                csize, _ = sizeof_struct(child, structs_by_name, constants)
                # Align this struct member to struct alignment (16) for nested,
                # or to the included struct's natural alignment (16) for included.
                local_next = align_up(local_next, STRUCT_ALIGN)
                array_n = evaluate_dim(m.array_decl, constants) if m.array_decl else 0
                count = max(array_n, 1)
                for i in range(count):
                    element_off = base_offset + local_next + i * csize
                    walk_child(child, element_off, f"{prefix}{m.name}.")
                local_next += csize * count
                if local_next > sizeof_struct_locally:
                    pass  # purely for clarity
            else:
                cpp = m.cpp_type
                # Resource-typed?
                if m.is_resource:
                    # SHADER_PARAMETER_STRUCT_REF is alignment STRUCT_ALIGN, others POINTER_ALIGN.
                    if m.macro == "SHADER_PARAMETER_STRUCT_REF":
                        align = STRUCT_ALIGN
                        elem_size = STRUCT_ALIGN
                    else:
                        align = POINTER_ALIGN
                        elem_size = POINTER_ALIGN
                    array_n = evaluate_dim(m.array_decl, constants) if m.array_decl else 0
                    if m.array_decl and array_n == 0:
                        # avoid silent skip; assume 1
                        array_n = 1
                    if array_n > 0:
                        local_next = align_up(local_next, align)
                        for i in range(array_n):
                            off = base_offset + local_next + i * POINTER_ALIGN
                            cur.resources.append(Resource(
                                name=f"{prefix}{m.name}",
                                ubmt_name=m.ubmt,
                                ubmt_value=UBMT[m.ubmt],
                                offset=off,
                                resource_index=0,  # assigned post-sort
                            ))
                        local_next += elem_size * array_n
                    else:
                        local_next = align_up(local_next, align)
                        off = base_offset + local_next
                        cur.resources.append(Resource(
                            name=f"{prefix}{m.name}",
                            ubmt_name=m.ubmt,
                            ubmt_value=UBMT[m.ubmt],
                            offset=off,
                            resource_index=0,
                        ))
                        local_next += elem_size
                else:
                    # numeric / matrix / vector member
                    # SHADER_PARAMETER_SCALAR_ARRAY expansion: pack to FUintVector4 / FIntVector4 / FVector4f
                    array_decl = m.array_decl
                    if m.macro == "SHADER_PARAMETER_SCALAR_ARRAY":
                        packed = SCALAR_ARRAY_PACK.get(cpp)
                        if not packed:
                            warn(f"  ! {s.cpp_name}.{m.name}: SHADER_PARAMETER_SCALAR_ARRAY type '{cpp}' unsupported -- skipping")
                            continue
                        scalar_n = evaluate_dim(array_decl, constants)
                        array_n = (scalar_n + 3) // 4
                        cpp = packed
                        array_decl = f"[{array_n}]"
                    info = TYPE_TABLE.get(cpp)
                    if info is None:
                        warn(f"  ! {s.cpp_name}.{m.name}: unknown C++ type '{cpp}' -- skipping")
                        continue
                    elem_size, elem_align, ubmt_no, hlsl_name, rows, cols = info
                    if array_decl:
                        # SHADER_PARAMETER_ARRAY: align to ARRAY_ELEM_ALIGN, then N * elem_size.
                        array_n = evaluate_dim(array_decl, constants)
                        if array_n <= 0:
                            warn(f"  ! {s.cpp_name}.{m.name}: array dim '{array_decl}' unresolved -- skipping")
                            continue
                        # Element size must be a multiple of 16; if not, layout is unsupported.
                        per_elem = max(elem_size, ARRAY_ELEM_ALIGN)
                        local_next = align_up(local_next, ARRAY_ELEM_ALIGN)
                        off = base_offset + local_next
                        cur.numeric.append(NumericMember(
                            name=f"{prefix}{m.name}", offset=off,
                            hlsl_type=hlsl_name, array_size=array_n,
                            num_rows=rows, num_columns=cols,
                        ))
                        local_next += per_elem * array_n
                        cur.max_numeric_end = max(cur.max_numeric_end, base_offset + local_next)
                    else:
                        local_next = align_up(local_next, elem_align)
                        off = base_offset + local_next
                        cur.numeric.append(NumericMember(
                            name=f"{prefix}{m.name}", offset=off,
                            hlsl_type=hlsl_name, array_size=0,
                            num_rows=rows, num_columns=cols,
                        ))
                        local_next += elem_size
                        cur.max_numeric_end = max(cur.max_numeric_end, base_offset + local_next)
        # NOTE: we don't need to surface local_next outside; walk_child below
        # reuses this nonlocal cursor of `cur` for resource collection; the
        # numeric offsets honored by `local_next` are encoded into NumericMember.offset.

    def walk_child(child: StructDef, base_offset: int, prefix: str) -> None:
        # walk_child is identical to walk except it uses an isolated cursor
        # so its offsets are relative to the parent's base_offset. We re-enter
        # walk(...) but with a per-call snapshot.
        nested_cur = _Cursor()

        def walk_inner(s: StructDef, off: int, pfx: str) -> None:
            ln = 0
            for m in s.members:
                if m.macro == "RENDER_TARGET_BINDING_SLOTS":
                    continue
                if m.is_struct_include or m.is_struct_nested:
                    if m.cpp_type not in structs_by_name:
                        warn(f"  ! {s.cpp_name}.{m.name}: nested/included struct '{m.cpp_type}' not found -- skipping")
                        continue
                    grand = structs_by_name[m.cpp_type]
                    g_size, _ = sizeof_struct(grand, structs_by_name, constants)
                    ln = align_up(ln, STRUCT_ALIGN)
                    array_n = evaluate_dim(m.array_decl, constants) if m.array_decl else 0
                    count = max(array_n, 1)
                    # HLSL-flattening rules from FShaderParametersMetadata::AddResourceTableEntriesRecursive
                    # (ShaderParameterMetadata.cpp:805-844 in UE 5.1.1, byte-identical in 5.4):
                    #   - INCLUDED struct (SHADER_PARAMETER_STRUCT_INCLUDE): prefix passes
                    #     through unchanged. The included struct's members are flattened
                    #     directly into the parent's namespace.
                    #   - NESTED non-array struct (SHADER_PARAMETER_STRUCT): MemberPrefix =
                    #     "<Prefix><MemberName>_".
                    #   - NESTED array struct (SHADER_PARAMETER_STRUCT_ARRAY): MemberPrefix
                    #     per element = "<Prefix><MemberName>_<index>_".
                    # The C# decompiler at RuntimeSymbolReader.cs:384 concatenates
                    # "<UBName>_<res.Name>", so res.Name in the JSON must be the
                    # underscore-flattened path WITHOUT the leading "<UBName>_".
                    if m.is_struct_include:
                        child_pfx_fn = lambda i: pfx
                    elif m.array_decl:
                        child_pfx_fn = lambda i: f"{pfx}{m.name}_{i}_"
                    else:
                        child_pfx_fn = lambda i: f"{pfx}{m.name}_"
                    for i in range(count):
                        walk_inner(grand, off + ln + i * g_size, child_pfx_fn(i))
                    ln += g_size * count
                elif m.is_resource:
                    align = STRUCT_ALIGN if m.macro == "SHADER_PARAMETER_STRUCT_REF" else POINTER_ALIGN
                    elem_size = align
                    array_n = evaluate_dim(m.array_decl, constants) if m.array_decl else 0
                    if m.array_decl and array_n == 0:
                        array_n = 1
                    if array_n > 0:
                        ln = align_up(ln, align)
                        for i in range(array_n):
                            res_off = off + ln + i * POINTER_ALIGN
                            cur.resources.append(Resource(
                                name=f"{pfx}{m.name}", ubmt_name=m.ubmt,
                                ubmt_value=UBMT[m.ubmt], offset=res_off,
                                resource_index=0,
                            ))
                        ln += elem_size * array_n
                    else:
                        ln = align_up(ln, align)
                        res_off = off + ln
                        cur.resources.append(Resource(
                            name=f"{pfx}{m.name}", ubmt_name=m.ubmt,
                            ubmt_value=UBMT[m.ubmt], offset=res_off,
                            resource_index=0,
                        ))
                        ln += elem_size
                else:
                    cpp = TYPE_ALIASES.get(m.cpp_type, m.cpp_type)
                    array_decl = m.array_decl
                    if m.macro == "SHADER_PARAMETER_SCALAR_ARRAY":
                        packed = SCALAR_ARRAY_PACK.get(cpp)
                        if not packed:
                            continue
                        scalar_n = evaluate_dim(array_decl, constants)
                        array_decl = f"[{(scalar_n + 3) // 4}]"
                        cpp = packed
                    info = TYPE_TABLE.get(cpp)
                    if info is None:
                        warn(f"  ! {s.cpp_name}.{m.name}: unknown C++ type '{cpp}' (nested) -- skipping")
                        continue
                    elem_size, elem_align, _, hlsl, rows, cols = info
                    if array_decl:
                        per_elem = max(elem_size, ARRAY_ELEM_ALIGN)
                        n = evaluate_dim(array_decl, constants)
                        if n <= 0:
                            continue
                        ln = align_up(ln, ARRAY_ELEM_ALIGN)
                        cur.numeric.append(NumericMember(
                            name=f"{pfx}{m.name}", offset=off + ln,
                            hlsl_type=hlsl, array_size=n, num_rows=rows, num_columns=cols,
                        ))
                        ln += per_elem * n
                        cur.max_numeric_end = max(cur.max_numeric_end, off + ln)
                    else:
                        ln = align_up(ln, elem_align)
                        cur.numeric.append(NumericMember(
                            name=f"{pfx}{m.name}", offset=off + ln,
                            hlsl_type=hlsl, array_size=0, num_rows=rows, num_columns=cols,
                        ))
                        ln += elem_size
                        cur.max_numeric_end = max(cur.max_numeric_end, off + ln)
            # ln is the relative end offset of `s` (size before final alignment).
        walk_inner(child, base_offset, prefix)

    # Top-level walk uses the same logic as walk_child but rooted at offset 0
    # and writing into the OUTER cursor `cur`. Simplest: reuse walk_child with
    # the top-level struct sd at base_offset=0.
    sizeof_struct_locally = 0  # placeholder to satisfy walk()'s closure
    walk_child(sd, 0, "")

    # Compute C++ struct size: ceiling of (max numeric offset+end, max resource offset+ptr_size)
    # then rounded up to STRUCT_ALIGN.
    struct_end = cur.max_numeric_end
    for r in cur.resources:
        struct_end = max(struct_end, r.offset + POINTER_ALIGN)
    struct_size = align_up(struct_end, STRUCT_ALIGN) if struct_end > 0 else 0

    # Resources sorted by MemberOffset (engine-side: ByMemberOffset comparator).
    cur.resources.sort(key=lambda r: (r.offset, r.ubmt_value))
    for i, r in enumerate(cur.resources):
        r.resource_index = i

    # Compute hash. Inputs:
    #   ConstantBufferSize = struct_size (sizeof of the C++ class incl. resource ptrs)
    #   BindingFlags = 1 (Shader) — same for global UBs; static-and-shader == 3 but
    #     the discriminator bit only checks `StaticSlot != INVALID`.
    #   StaticSlot bit comes from the IMPLEMENT_STATIC_*_STRUCT macro that named
    #     this UB; we set this from the IMPLEMENT registry.
    cb_size_for_hash = struct_size
    # Default to Shader binding; the main loop overrides this with whatever the
    # IMPLEMENT_* macro declared (see compute_hash callsite in main()).
    layout_hash = compute_hash(cb_size_for_hash, binding_flags=1, has_static_slot=False,
                               resources=cur.resources)

    # constantBufferSize in the JSON is the HLSL cbuffer-portion size — sum of
    # numeric (CB-stored) member sizes. Existing seed JSONs use the raw end-of-
    # numeric offset without padding (e.g. LocalVF = 24 for Int4+Int+UInt), so
    # we match that convention. Hash continues to use the C++ struct sizeof.
    cb_size_json = cur.max_numeric_end if cur.max_numeric_end else struct_size
    return LayoutResult(
        cpp_name=sd.cpp_name,
        numeric=cur.numeric,
        resources=cur.resources,
        struct_size=struct_size,
        cb_size=cb_size_json,
        layout_hash=layout_hash,
    )


def sizeof_struct(s: StructDef, structs_by_name: dict[str, StructDef], constants: dict[str, int] | None = None) -> tuple[int, int]:
    """Returns ``(size, alignment)`` of a child struct (used for nested/included
    expansions). Walks members the same way as compute_layout but without
    recording into the outer cursor. ``alignment`` is always STRUCT_ALIGN.

    ``constants`` MUST be passed so array dims like ``[GMaxForwardShadowCascades]``
    resolve to their real value (4). Without it the old ``int(array_decl.strip)``
    fallback returned 1 for any identifier-keyed dim, undersizing every nested
    struct that contained an array — which then made the OUTER walker place
    the next nested struct too early and OVERLAP the previous one's tail.
    Optional only so legacy call-sites compile; pass-through is required for
    layout correctness."""
    cn = constants or {}
    def dim(decl: str) -> int:
        n = evaluate_dim(decl, cn) if decl else 0
        return n if n > 0 else 1
    ln = 0
    for m in s.members:
        if m.macro == "RENDER_TARGET_BINDING_SLOTS":
            continue
        if m.is_struct_include or m.is_struct_nested:
            child = structs_by_name.get(m.cpp_type)
            if child is None:
                continue
            csize, _ = sizeof_struct(child, structs_by_name, cn)
            ln = align_up(ln, STRUCT_ALIGN)
            n = dim(m.array_decl) if m.array_decl else 1
            ln += csize * n
        elif m.is_resource:
            align = STRUCT_ALIGN if m.macro == "SHADER_PARAMETER_STRUCT_REF" else POINTER_ALIGN
            elem_size = align
            n = dim(m.array_decl) if m.array_decl else 1
            ln = align_up(ln, align)
            ln += elem_size * n
        else:
            cpp = TYPE_ALIASES.get(m.cpp_type, m.cpp_type)
            array_decl = m.array_decl
            if m.macro == "SHADER_PARAMETER_SCALAR_ARRAY":
                packed = SCALAR_ARRAY_PACK.get(cpp)
                if not packed:
                    continue
                scalar_n = dim(array_decl)
                array_decl = f"[{(scalar_n + 3) // 4}]"
                cpp = packed
            info = TYPE_TABLE.get(cpp)
            if info is None:
                continue
            elem_size, elem_align, _, _, _, _ = info
            if array_decl:
                per_elem = max(elem_size, ARRAY_ELEM_ALIGN)
                n = dim(array_decl)
                ln = align_up(ln, ARRAY_ELEM_ALIGN)
                ln += per_elem * n
            else:
                ln = align_up(ln, elem_align)
                ln += elem_size
    return align_up(ln, STRUCT_ALIGN), STRUCT_ALIGN


def compute_hash(constant_buffer_size: int, binding_flags: int, has_static_slot: bool,
                 resources: list[Resource]) -> int:
    """Re-implements ``FRHIUniformBufferLayoutInitializer::ComputeHash`` byte-for-byte.

    Source: ``RHIResources.h:806-836`` (5.1) / ``RHIUniformBufferLayoutInitializer.h:62-92`` (5.4)
    """
    h = ((constant_buffer_size & 0xFFFF) << 16) \
        | ((binding_flags & 0xFF) << 8) \
        | (1 if has_static_slot else 0)
    h &= 0xFFFFFFFF
    for r in resources:
        h ^= r.offset & 0xFFFF  # MemberOffset is uint16 on the engine side
    # XOR-fold MemberType in batches of 4 / 2 / 1, consuming Resources from the END.
    n = len(resources)
    while n >= 4:
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF) << 0
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF) << 8
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF) << 16
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF) << 24
    while n >= 2:
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF) << 0
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF) << 16
    while n > 0:
        n -= 1; h ^= (resources[n].ubmt_value & 0xFF)
    return h & 0xFFFFFFFF


# ---------------------------------------------------------------------------
# JSON emitter
# ---------------------------------------------------------------------------

# Map HLSL type name -> (ShaderParamType enum string, RowCount, ColumnCount, IsMatrix).
# Mirrors Ruri.ShaderTools.ShaderParamType + the loader's HLSL parser at
# EngineUbMetadataLoader.ParseType. Keeping the table here keeps the
# generator self-contained (no shared file).
#
# Convention (matches `MaterialConstantBufferReader.AddVectorMember` and the
# downstream consumer at `Ruri.ShaderDecompiler/Spirv/Rewriter/Helpers/
# LayoutBuilder.cs::TryCreateLogicalTypeFromMetadata`):
#   * Scalar (Float, Int, ...):   RowCount=1, ColumnCount=1
#   * Vector (FloatN, IntN, ...): RowCount=N, ColumnCount=1
#     ^ RowCount is the COMPONENT COUNT, NOT an HLSL row/col semantic.
#       The consumer reads RowCount==1 as "scalar" and RowCount>1 as
#       "vector with N components" — emitting `1 x N` for Float4 etc.
#       silently collapses every vector field to a scalar in the rewriter.
#   * Matrix (Float4x4, ...):     RowCount=rows, ColumnCount=cols, IsMatrix=True
_HLSL_TO_TYPE: dict[str, tuple[str, int, int, bool]] = {
    "Float":     ("Float", 1, 1, False),
    "Float2":    ("Float", 2, 1, False),
    "Float3":    ("Float", 3, 1, False),
    "Float4":    ("Float", 4, 1, False),
    "Int":       ("Int",   1, 1, False),
    "Int2":      ("Int",   2, 1, False),
    "Int3":      ("Int",   3, 1, False),
    "Int4":      ("Int",   4, 1, False),
    "UInt":      ("UInt",  1, 1, False),
    "UInt2":     ("UInt",  2, 1, False),
    "UInt3":     ("UInt",  3, 1, False),
    "UInt4":     ("UInt",  4, 1, False),
    "Bool":      ("Bool",  1, 1, False),
    "Bool2":     ("Bool",  2, 1, False),
    "Bool3":     ("Bool",  3, 1, False),
    "Bool4":     ("Bool",  4, 1, False),
    "Half":      ("Half",  1, 1, False),
    "Half2":     ("Half",  2, 1, False),
    "Half3":     ("Half",  3, 1, False),
    "Half4":     ("Half",  4, 1, False),
    "Float4x4":  ("Float", 4, 4, True),
    "Float3x4":  ("Float", 3, 4, True),
    "Float4x3":  ("Float", 4, 3, True),
    "Float3x3":  ("Float", 3, 3, True),
}


def _numeric_to_vector_or_matrix(m: NumericMember) -> tuple[str, dict]:
    """Classify a NumericMember as either a VectorParameter or MatrixParameter
    payload (PascalCase wire fields). Returns ("vector"|"matrix", dict)."""
    entry = _HLSL_TO_TYPE.get(m.hlsl_type)
    if entry is None:
        # Fallback: trust num_rows/num_columns. Assume Float scalar.
        scalar, rows, cols, is_matrix = "Float", m.num_rows or 1, m.num_columns or 1, (m.num_rows or 0) > 1
    else:
        scalar, rows, cols, is_matrix = entry
        # NumericMember rows/cols are authoritative when present and the table
        # row/col don't match (defensive; should rarely happen for known types).
        if m.num_rows and m.num_columns and (rows, cols) != (m.num_rows, m.num_columns):
            rows, cols = m.num_rows, m.num_columns
            is_matrix = rows > 1
    payload = {
        "Name": m.name,
        "NameIndex": -1,
        "Index": m.offset,
        "ArraySize": m.array_size,
        "Type": scalar,
        "RowCount": rows,
        "ColumnCount": cols,
        "IsMatrix": is_matrix,
    }
    return ("matrix" if is_matrix else "vector"), payload


# Group resources into typed buckets matching the C# loader's standard-type
# split (Textures / Samplers / Buffers / UAVs). Same classification logic
# as EngineUbMetadataRegistry.EnsureTypedBucketsPopulated.
_TEXTURE_UBMT = {"UBMT_TEXTURE", "UBMT_RDG_TEXTURE", "UBMT_RDG_TEXTURE_ACCESS",
                 "UBMT_RDG_TEXTURE_ACCESS_ARRAY"}
_SAMPLER_UBMT = {"UBMT_SAMPLER"}
_UAV_UBMT     = {"UBMT_UAV", "UBMT_RDG_TEXTURE_UAV", "UBMT_RDG_BUFFER_UAV"}


def to_json(meta_name: str, engine_version: str, engine_source: str,
            layout_hash: int, binding_flags: str, cb_size: int,
            members: list[NumericMember], resources: list[Resource]) -> str:
    """Emit the unified JSON schema: a thin metadata wrapper composing the
    standard parameter types (ConstantBufferParameter + Texture/Sampler/
    Buffer/UAV lists) plus a canonical engine-side Resources flat list.

    PascalCase wire format — matches Newtonsoft.Json defaults used by the
    rest of the Ruri.ShaderDecompiler pipeline. The C# loader runs with
    PropertyNameCaseInsensitive=true so either casing decodes, but we emit
    PascalCase consistently.
    """
    vectors: list[dict] = []
    matrices: list[dict] = []
    for m in members:
        kind, payload = _numeric_to_vector_or_matrix(m)
        if kind == "matrix":
            matrices.append(payload)
        else:
            vectors.append(payload)

    constant_buffer = {
        "Name": meta_name,
        "NameIndex": -1,
        "MatrixParameters": matrices,
        "VectorParameters": vectors,
        "StructParameters": [],
        "Size": cb_size,
        "IsPartialCB": False,
    }

    # Typed bucket views — pre-classified for consumers that don't want to
    # walk Resources themselves. Each entry's `Index` field holds the engine
    # resource-table position (matches SRT record.ResourceIndex).
    textures: list[dict] = []
    samplers: list[dict] = []
    buffers: list[dict] = []
    uavs: list[dict] = []
    for r in resources:
        full_type = "UBMT_" + r.ubmt_name
        if full_type in _TEXTURE_UBMT:
            textures.append({
                "Name": r.name,
                "NameIndex": -1,
                "Index": r.resource_index,
                "SamplerIndex": -1,
                "MultiSampled": False,
                "Dim": 2,
            })
        elif full_type in _SAMPLER_UBMT:
            samplers.append({
                "Name": r.name,
                "Sampler": r.resource_index,
                "BindPoint": r.resource_index,
            })
        elif full_type in _UAV_UBMT:
            uavs.append({
                "Name": r.name,
                "NameIndex": -1,
                "Index": r.resource_index,
                "OriginalIndex": r.resource_index,
            })
        else:
            buffers.append({
                "Name": r.name,
                "NameIndex": -1,
                "Index": r.resource_index,
                "ArraySize": 0,
            })

    # Canonical engine-side flat resource list — 1:1 with
    # FRHIUniformBufferLayoutInitializer.Resources[], sorted by MemberOffset.
    # Source of truth for hash verification and SRT lookup.
    flat_resources = [
        {
            "Index": r.resource_index,
            "Offset": r.offset,
            "Name": r.name,
            "UbmtType": "UBMT_" + r.ubmt_name,
        }
        for r in resources
    ]

    obj = {
        "Name": meta_name,
        "EngineVersion": engine_version,
        "EngineSource": engine_source,
        "LayoutHash": f"0x{layout_hash:08X}",
        "BindingFlags": binding_flags,
        "ConstantBuffer": constant_buffer,
        "Textures": textures,
        "Samplers": samplers,
        "Buffers": buffers,
        "UAVs": uavs,
        "Resources": flat_resources,
    }
    return json.dumps(obj, indent=2, ensure_ascii=False) + "\n"


# ---------------------------------------------------------------------------
# Main driver
# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--engine-src", required=True, type=Path, help="Path to UE source root (folder containing 'Engine/Source/').")
    ap.add_argument("--engine-version", required=True, help="Engine version string for the JSON 'engineVersion' field, e.g. '5.1.1'.")
    ap.add_argument("--out-dir", type=Path, help="Output root for EngineUbMetadata (parent of GAME_UE5_X folders).")
    ap.add_argument("--target-folder", help="Per-game folder name (e.g. 'GAME_UE5_1', 'GAME_UE5_4'). Created if missing.")
    ap.add_argument("--ub-filter", default="", help="Optional regex on UB shader-binding name (or C++ type). When set, only matching UBs are emitted.")
    ap.add_argument("--validate", type=Path, help="Validate-only: compare generated hashes/sizes against JSONs in this folder. No writes.")
    ap.add_argument("--verbose", "-v", action="store_true", help="Print every warning/skip.")
    ap.add_argument("--list-only", action="store_true", help="Only list the UBs discovered, don't emit JSON.")
    ap.add_argument("--emit-shader-type-seeds", action="store_true",
                    help="Also dump ShaderType / $Globals loose-parameter name catalogues under "
                         "<out>/<target>/_ShaderType/. Best-effort: $Globals offsets aren't "
                         "recoverable from source, files carry placeholder offsets and a Debug "
                         "block noting the limitation.")
    args = ap.parse_args()

    engine_src: Path = args.engine_src.resolve()
    if not (engine_src / "Engine" / "Source").exists():
        print(f"ERROR: --engine-src {engine_src} does not look like a UE source root (no Engine/Source).", file=sys.stderr)
        return 2

    def warn(msg: str) -> None:
        if args.verbose:
            print(msg)

    print(f"[gen] scanning {engine_src} ...")
    constants = collect_constants(engine_src)
    print(f"[gen] collected {len(constants)} constants")
    macro_tables = collect_macro_tables(engine_src)
    print(f"[gen] collected {len(macro_tables)} macro tables")
    ub_name_map = collect_ub_name_map(engine_src)
    print(f"[gen] collected {len(ub_name_map)} IMPLEMENT_*_STRUCT mappings")

    # Pass: parse every BEGIN_..._STRUCT block.
    structs: list[StructDef] = []
    for fp in iter_cpp_files(engine_src):
        try:
            text = read_text(fp)
        except OSError:
            continue
        if "BEGIN_" not in text:
            continue
        blocks = find_struct_blocks(text)
        if not blocks:
            continue
        line_table = build_line_table(text)
        for start, end, name, kind in blocks:
            sd = parse_struct_block(text, start, end, name, kind, str(fp), line_table, macro_tables)
            structs.append(sd)
    print(f"[gen] parsed {len(structs)} struct blocks")

    structs_by_name = {s.cpp_name: s for s in structs}

    ub_structs = [s for s in structs if s.kind == "ub"]
    print(f"[gen] {len(ub_structs)} uniform buffers (BEGIN_GLOBAL_/UNIFORM_BUFFER_STRUCT)")

    ub_re = re.compile(args.ub_filter) if args.ub_filter else None

    successes = failures = collisions = 0
    out_dir: Path | None = None
    if args.out_dir and args.target_folder:
        out_dir = args.out_dir.resolve() / args.target_folder
        if not args.validate and not args.list_only:
            out_dir.mkdir(parents=True, exist_ok=True)

    validate_index: dict[tuple[str, str], dict] = {}
    if args.validate:
        for fp in args.validate.glob("*_MetaData.json"):
            try:
                obj = json.loads(fp.read_text(encoding="utf-8-sig"))
                # Tolerate both PascalCase (new) and camelCase (legacy) keys.
                name = obj.get("Name") or obj.get("name", "")
                hash_str = obj.get("LayoutHash") or obj.get("layoutHash", "")
                key = (name, hash_str.lower())
                validate_index[key] = obj
            except Exception as exc:  # noqa: BLE001
                print(f"  ! could not read {fp}: {exc}")

    # Track which (name, hash) we've already written so two C++ structs that
    # IMPLEMENT to the same shader binding name don't clobber each other.
    seen: dict[tuple[str, int], str] = {}

    for sd in sorted(ub_structs, key=lambda s: s.cpp_name):
        mapped = ub_name_map.get(sd.cpp_name)
        if not mapped:
            # Use the C++ struct name with the leading 'F' stripped as a fallback;
            # otherwise IMPLEMENT was probably in a file we missed.
            shader_name = sd.cpp_name[1:] if sd.cpp_name.startswith("F") else sd.cpp_name
            static_slot = ""
            binding_flags_value = 1
            has_static_slot_bit = False
        else:
            shader_name, static_slot, _, binding_flags_value, has_static_slot_bit = mapped

        if ub_re and not (ub_re.search(shader_name) or ub_re.search(sd.cpp_name)):
            continue

        layout = compute_layout(sd, structs_by_name, constants, warn)
        if layout is None:
            failures += 1
            continue

        # Hash always uses the binding flags + static-slot bit from the IMPLEMENT
        # macro, not the BEGIN block (which only declares the C++ struct layout).
        layout.layout_hash = compute_hash(layout.struct_size,
                                          binding_flags=binding_flags_value,
                                          has_static_slot=has_static_slot_bit,
                                          resources=layout.resources)

        eng_src_rel = engine_relative(sd.src_file, engine_src)
        eng_src_field = f"{eng_src_rel}:{sd.src_line} ({sd.cpp_name})"

        if binding_flags_value == 3:
            binding_flags = "StaticAndShader"
        elif binding_flags_value == 2:
            binding_flags = "Static"
        else:
            binding_flags = "Shader"

        if args.list_only:
            print(f"  {shader_name:32s} ({sd.cpp_name:48s}) hash=0x{layout.layout_hash:08X} "
                  f"members={len(layout.numeric):4d} res={len(layout.resources):4d} cbSize={layout.cb_size}")
            continue

        json_text = to_json(
            meta_name=shader_name,
            engine_version=args.engine_version,
            engine_source=eng_src_field,
            layout_hash=layout.layout_hash,
            binding_flags=binding_flags,
            cb_size=layout.cb_size,
            members=layout.numeric,
            resources=layout.resources,
        )

        if args.validate:
            key = (shader_name, f"0x{layout.layout_hash:08x}")
            ref = validate_index.get(key)
            if ref is None:
                # try uppercase
                key_u = (shader_name, f"0x{layout.layout_hash:08X}".lower())
                ref = validate_index.get(key_u)
            if ref is None:
                hashes_for_name = [k[1] for k in validate_index if k[0] == shader_name]
                if hashes_for_name:
                    print(f"  X {shader_name}: computed 0x{layout.layout_hash:08X} but existing JSON(s) hash {hashes_for_name}")
                    failures += 1
                else:
                    if args.verbose:
                        print(f"  . {shader_name}: no existing JSON to compare")
                continue
            # OK, hash matched. Compare key fields (tolerate both legacy
            # camelCase and the new PascalCase schema).
            r_cb = (
                ref.get("constantBufferSize")
                or (ref.get("ConstantBuffer") or {}).get("Size", 0)
                or 0
            )
            r_res = len(ref.get("Resources") or ref.get("resources", []))
            r_mem_legacy = ref.get("members", [])
            r_mem_new = (ref.get("ConstantBuffer") or {})
            r_mem = (
                len(r_mem_legacy)
                if r_mem_legacy
                else (len(r_mem_new.get("VectorParameters", []))
                      + len(r_mem_new.get("MatrixParameters", [])))
            )
            ok = (r_res == len(layout.resources))
            tag = "OK" if ok else "?"
            print(f"  {tag} {shader_name:28s} hash=0x{layout.layout_hash:08X} "
                  f"cb gen={layout.cb_size} ref={r_cb} | res gen={len(layout.resources)} ref={r_res} | "
                  f"members gen={len(layout.numeric)} ref={r_mem}")
            if ok:
                successes += 1
            else:
                failures += 1
            continue

        # Emit JSON
        key = (shader_name, layout.layout_hash)
        if key in seen:
            collisions += 1
            warn(f"  ~ {shader_name} 0x{layout.layout_hash:08X} already produced by {seen[key]}, keeping first")
            continue
        seen[key] = sd.cpp_name

        assert out_dir is not None  # for type-checkers; --out-dir is required when emitting
        out_path = out_dir / f"{shader_name}_{layout.layout_hash:08X}_MetaData.json"
        out_path.write_text(json_text, encoding="utf-8")
        successes += 1
        if args.verbose:
            print(f"  + {out_path.name}  ({sd.cpp_name})")

    print(f"[gen] done. ok={successes} skipped/failed={failures} dup={collisions}")

    if args.emit_shader_type_seeds and out_dir is not None and not args.validate and not args.list_only:
        try:
            emit_shader_type_seeds(out_dir, args.engine_version, engine_src)
        except NotImplementedError as exc:
            print(f"[gen][shader-type] {exc}")

    return 0 if failures == 0 else 1


def engine_relative(abs_path: str, engine_src: Path) -> str:
    """Best-effort relative path under the engine root for the JSON engineSource field."""
    try:
        return str(Path(abs_path).resolve().relative_to(engine_src)).replace("\\", "/")
    except ValueError:
        return abs_path.replace("\\", "/")


# ---------------------------------------------------------------------------
# ShaderType / $Globals seed extraction (best-effort skeleton)
# ---------------------------------------------------------------------------
#
# UE FShader subclasses bind loose shader parameters into the $Globals cbuffer
# (and direct texture/sampler slots) via:
#     LAYOUT_FIELD(FShaderParameter,         <name>);    // numeric (CB)
#     LAYOUT_FIELD(FShaderResourceParameter, <name>);    // tex/sampler/SRV/UAV
# Each FShader::Bind(Initializer.ParameterMap, TEXT("<name>")) call at
# constructor time resolves the source-level <name> against the cooked
# shader's ParameterMap. After cook the ParameterMap is dropped and only
# offsets+types survive in the cooked binary — we can recover NAMES from
# source but NOT their byte offsets (they depend on DXC's $Globals layout
# of the actual HLSL source, which the C++ class only references by name).
#
# What this pass emits:
#   <out>/_ShaderType/<ClassName>_<HashedName:016X>_MetaData.json
# where HashedName = CityHash64(upper(ClassName), seed=0), matching UE's
# FHashedName mechanism (see RuntimeSymbolReader.HashedNamesResolver in C#).
#
# Each file follows the SAME schema as engine-UB seeds (ConstantBuffer +
# Textures/Samplers/Buffers/UAVs + Resources) so the loader plumbing reuses
# the existing typed wire format. ConstantBuffer.Name = "$Globals", and
# ConstantBuffer entries carry NAMES + SEQUENTIAL placeholder offsets in
# source declaration order (16-byte stride) — the downstream loader needs
# to reconcile against actual cooked offsets via name-driven heuristics.
# The "Debug" dict records the source file/line so users can grep back.
#
# Why best-effort: $Globals offsets aren't recoverable from source (DXC
# assigns them per its own packing rules on the HLSL source — not the C++
# declaration order). See `Source/Ruri.ShaderDecompiler/CLAUDE.md` lines
# 338-348 for the full story. Treat the JSON as a name catalogue keyed by
# ShaderType hash; consumers MUST validate offsets against the cooked
# binary before trusting them.

_LAYOUT_FIELD_RE = re.compile(
    r"LAYOUT_FIELD(?:_INITIALIZED)?\(\s*"
    r"(FShaderParameter|FShaderResourceParameter|FShaderResourceParameterArray|FRWShaderParameter)\s*,\s*"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*[\),]",
)

_CLASS_OPEN_RE = re.compile(
    r"^\s*class\s+(?:[A-Z_]+_API\s+)?([A-Za-z_][A-Za-z0-9_]*)"
    r"(?:\s*<[^>]+>)?"
    r"(?:\s*:\s*(?:public|protected|private)\s+([^\s{,]+))?",
    re.MULTILINE,
)


def collect_shader_type_layouts(engine_src: Path) -> list[tuple[str, str, str, int, list[tuple[str, str, str]]]]:
    """Walk engine source for `class X : public FShader/FGlobalShader/
    FMeshMaterialShader/...` declarations that contain LAYOUT_FIELD()
    bindings. Returns a list of
        (class_name, parent_name, src_file, src_line, fields)
    where `fields` is `[(layout_type, field_name, raw_decl), ...]`.

    Best-effort source scan. Does not understand templates, multiple
    inheritance, or nested classes deeply — these would need a full C++
    parser. The output is a name catalogue; downstream consumers validate
    against cooked offsets.
    """
    out: list[tuple[str, str, str, int, list[tuple[str, str, str]]]] = []
    bases_of_interest = {
        "FShader", "FGlobalShader", "FMeshMaterialShader", "FMaterialShader",
        "FNiagaraShader", "FNaniteGlobalShader",
    }
    for fp in iter_cpp_files(engine_src):
        try:
            text = read_text(fp)
        except OSError:
            continue
        if "LAYOUT_FIELD" not in text:
            continue
        line_table = build_line_table(text)
        # Iterate class openings and pair each with its LAYOUT_FIELDs up to
        # the matching '};'. This is a simple brace-balance pass — robust
        # against typical UE style but not against macro magic that hides
        # the class-end token from regex matching.
        for cm in _CLASS_OPEN_RE.finditer(text):
            cname = cm.group(1)
            parent = cm.group(2) or ""
            # Filter: parent must be in our known set, OR class name starts
            # with `T...PS`/`T...VS`/`T...CS` etc. (mesh-material shader
            # template form). We're conservative.
            if parent not in bases_of_interest:
                continue
            # Find '{' after the class header and the matching '};'.
            brace_open = text.find("{", cm.end())
            if brace_open < 0:
                continue
            depth = 1
            i = brace_open + 1
            while i < len(text) and depth > 0:
                ch = text[i]
                if ch == "{": depth += 1
                elif ch == "}": depth -= 1
                i += 1
            class_body = text[brace_open:i]
            fields: list[tuple[str, str, str]] = []
            for lm in _LAYOUT_FIELD_RE.finditer(class_body):
                ltype = lm.group(1)
                fname = lm.group(2)
                fields.append((ltype, fname, lm.group(0)))
            if not fields:
                continue
            src_line = bisect_line(line_table, cm.start())
            out.append((cname, parent, str(fp), src_line, fields))
    return out


# CityHash64 port for FHashedName — mirrors UE 5.x canonical CityHash 1.1.0
# byte-for-byte. Reference source (Google + UE identical impl):
#   D:/GameStudy/UnrealEngine-5.1.1-release/Engine/Source/Runtime/Core/Private/Hash/CityHash.cpp
# (Both UE and Google's reference 1.1.0 use the SAME constants. Some other
# C#/JS ports floating around — including the in-tree `HashedNamesResolver.cs`
# — substitute kMul for k1/k2 in places and produce wrong hashes; do not
# copy from those. The values below match `CityHash_Internal::k0/k1/k2`
# from CityHash.cpp:122-124 exactly.)
#
# Python's ints are unbounded so every arithmetic step masks to 64 bits.

_K0 = 0xc3a5c85c97cb3127  # CityHash_Internal::k0
_K1 = 0xb492b66fbe98f273  # CityHash_Internal::k1  (used in the >64-byte loop)
_K2 = 0x9ae16a3b2f90404f  # CityHash_Internal::k2  (used in HashLen0to16/17to32/33to64)
_K_MUL_HASH16 = 0x9ddfea08eb382d69  # Murmur kMul used ONLY inside HashLen16(u,v) 2-arg form
_MASK64 = 0xFFFFFFFFFFFFFFFF


def _u64(x: int) -> int:
    return x & _MASK64


def _rotr(val: int, shift: int) -> int:
    val &= _MASK64
    return ((val >> shift) | (val << (64 - shift))) & _MASK64


def _reverse_bytes(value: int) -> int:
    return int.from_bytes(_u64(value).to_bytes(8, "little"), "big")


def _shift_mix(val: int) -> int:
    val &= _MASK64
    return val ^ (val >> 47)


def _fetch32(s: bytes, pos: int) -> int:
    return int.from_bytes(s[pos:pos + 4], "little")


def _fetch64(s: bytes, pos: int) -> int:
    return int.from_bytes(s[pos:pos + 8], "little")


# Two-arg HashLen16(u,v) — the Murmur-inspired final-mix. Uses a DEDICATED
# constant 0x9ddfea08eb382d69 (Hash128to64::kMul), not k1/k2.
def _hash_len16_2(u: int, v: int) -> int:
    return _hash_len16_3(u, v, _K_MUL_HASH16)


# Three-arg HashLen16(u,v,mul) — caller picks the multiplier. CityHash64
# internal uses this with mul=k2+2*len for the medium-length paths and
# mul=k1 inside the >64 main loop's final mix is via the 2-arg form, NOT
# this 3-arg one. So this only feeds the small-string paths.
def _hash_len16_3(u: int, v: int, mul: int) -> int:
    a = _u64((u ^ v) * mul)
    a ^= a >> 47
    b = _u64((v ^ a) * mul)
    b ^= b >> 47
    return _u64(b * mul)


def _hash_len_0_to_16(s: bytes) -> int:
    length = len(s)
    if length >= 8:
        mul = _u64(_K2 + length * 2)
        a = _u64(_fetch64(s, 0) + _K2)
        b = _fetch64(s, length - 8)
        c = _u64(_rotr(b, 37) * mul + a)
        d = _u64((_rotr(a, 25) + b) * mul)
        return _hash_len16_3(c, d, mul)
    if length >= 4:
        mul = _u64(_K2 + length * 2)
        a = _fetch32(s, 0)
        return _hash_len16_3(length + (a << 3), _fetch32(s, length - 4), mul)
    if length > 0:
        a = s[0]
        b = s[length >> 1]
        c = s[length - 1]
        y = a + (b << 8)
        z = length + (c << 2)
        return _u64(_shift_mix(_u64(y * _K2) ^ _u64(z * _K0)) * _K2)
    return _K2


def _hash_len_17_to_32(s: bytes) -> int:
    length = len(s)
    mul = _u64(_K2 + length * 2)
    a = _u64(_fetch64(s, 0) * _K1)
    b = _fetch64(s, 8)
    c = _u64(_fetch64(s, length - 8) * mul)
    d = _u64(_fetch64(s, length - 16) * _K2)
    return _hash_len16_3(
        _u64(_rotr(_u64(a + b), 43) + _rotr(c, 30) + d),
        _u64(a + _rotr(_u64(b + _K2), 18) + c),
        mul,
    )


def _hash_len_33_to_64(s: bytes) -> int:
    length = len(s)
    mul = _u64(_K2 + length * 2)
    a = _u64(_fetch64(s, 0) * _K2)
    b = _fetch64(s, 8)
    c = _fetch64(s, length - 24)
    d = _fetch64(s, length - 32)
    e = _u64(_fetch64(s, 16) * _K2)
    f = _u64(_fetch64(s, 24) * 9)
    g = _fetch64(s, length - 8)
    h = _u64(_fetch64(s, length - 16) * mul)
    u = _u64(_rotr(_u64(a + g), 43) + _u64((_rotr(b, 30) + c) * 9))
    v = _u64(((a + g) ^ d) + f + 1)
    w = _u64(_reverse_bytes(_u64((u + v) * mul)) + h)
    x = _u64(_rotr(_u64(e + f), 42) + c)
    y = _u64((_reverse_bytes(_u64((v + w) * mul)) + g) * mul)
    z = _u64(e + f + c)
    a = _u64(_reverse_bytes(_u64((x + z) * mul + y)) + b)
    b = _u64(_shift_mix(_u64((z + a) * mul + d + h)) * mul)
    return _u64(b + x)


def _weak_hash_len32_with_seeds(s: bytes, offset: int, a: int, b: int) -> tuple[int, int]:
    w = _fetch64(s, offset)
    x = _fetch64(s, offset + 8)
    y = _fetch64(s, offset + 16)
    z = _fetch64(s, offset + 24)
    a = _u64(a + w)
    b = _rotr(_u64(b + a + z), 21)
    c = a
    a = _u64(a + x + y)
    b = _u64(b + _rotr(a, 44))
    return _u64(a + z), _u64(b + c)


def _city_hash_64(s: bytes) -> int:
    length = len(s)
    if length <= 16:
        return _hash_len_0_to_16(s)
    if length <= 32:
        return _hash_len_17_to_32(s)
    if length <= 64:
        return _hash_len_33_to_64(s)

    # Long-string path: 56 bytes of running state, k1-driven main loop.
    x = _fetch64(s, length - 40)
    y = _u64(_fetch64(s, length - 16) + _fetch64(s, length - 56))
    z = _hash_len16_2(_u64(_fetch64(s, length - 48) + length), _fetch64(s, length - 24))
    v = _weak_hash_len32_with_seeds(s, length - 64, length, z)
    w = _weak_hash_len32_with_seeds(s, length - 32, _u64(y + _K1), x)
    x = _u64(x * _K1 + _fetch64(s, 0))

    offset = 0
    length_iter = (length - 1) & ~63
    while length_iter != 0:
        x = _u64(_rotr(_u64(x + y + v[0] + _fetch64(s, offset + 8)), 37) * _K1)
        y = _u64(_rotr(_u64(y + v[1] + _fetch64(s, offset + 48)), 42) * _K1)
        x ^= w[1]
        y = _u64(y + v[0] + _fetch64(s, offset + 40))
        z = _u64(_rotr(_u64(z + w[0]), 33) * _K1)
        v = _weak_hash_len32_with_seeds(s, offset, _u64(v[1] * _K1), _u64(x + w[0]))
        w = _weak_hash_len32_with_seeds(s, offset + 32, _u64(z + w[1]), _u64(y + _fetch64(s, offset + 16)))
        x, z = z, x
        offset += 64
        length_iter -= 64

    # Final mix uses 2-arg HashLen16 (kMul) and `* k1` for the y term.
    return _hash_len16_2(
        _u64(_hash_len16_2(v[0], w[0]) + _u64(_shift_mix(y) * _K1) + z),
        _u64(_hash_len16_2(v[1], w[1]) + x),
    )


def city_hash_64_with_seed(name: str, seed: int = 0) -> int:
    """FHashedName-equivalent hash. UE's `FHashedName(FName)` UPPER-cases the
    name and runs `CityHash64WithSeed(upper, len, internalNumber)`. For
    shader/struct type FNames the number is 0. The canonical UE impl
    (CityHash.cpp:430-440) is:
        CityHash64WithSeed(s, len, seed) =
            CityHash64WithSeeds(s, len, k2, seed) =
            HashLen16(CityHash64(s) - k2, seed)
    Returns the 64-bit hash as an int."""
    upper = name.upper().encode("utf-8")
    return _hash_len16_2(_u64(_city_hash_64(upper) - _K2), seed)


def emit_shader_type_seeds(out_dir: Path, engine_version: str, engine_src: Path) -> int:
    """Emit one `<ClassName>_<HashedName:016X>_MetaData.json` per FShader
    derivative under `out_dir/_ShaderType/`. Returns the count written.

    SKELETON: currently logs the count of discovered classes/fields but
    does not write because the CityHash64 port isn't included here. Wire
    up by porting the hash function from HashedNamesResolver.cs and
    uncommenting the emission block.
    """
    classes = collect_shader_type_layouts(engine_src)
    total_fields = sum(len(fs) for _, _, _, _, fs in classes)
    print(f"[gen][shader-type] discovered {len(classes)} FShader-derivative class(es) "
          f"with {total_fields} LAYOUT_FIELD bindings.")
    if not classes:
        return 0

    target = out_dir / "_ShaderType"
    target.mkdir(parents=True, exist_ok=True)

    written = 0
    for cname, parent, src_file, src_line, fields in classes:
        # Hashed-name suffix matches FShaderType::HashedName — UPPER-cased
        # UTF-8 bytes through CityHash64WithSeed(0). The same hash appears
        # in cooked FShaderMapPointerTable.Types[i].Hash, so this filename
        # is directly addressable from cook data.
        hash_token = f"{city_hash_64_with_seed(cname):016X}"

        vectors: list[dict] = []
        textures: list[dict] = []
        samplers: list[dict] = []
        buffers: list[dict] = []
        uavs: list[dict] = []
        flat_resources: list[dict] = []
        # Placeholder sequential offsets at 16-byte stride. The COOK
        # assigns real $Globals offsets per DXC packing of the HLSL source,
        # which we don't have — the loader must reconcile by name.
        next_cb_offset = 0
        res_index = 0
        for ltype, fname, _raw in fields:
            if ltype == "FShaderParameter":
                vectors.append({
                    "Name": fname,
                    "NameIndex": -1,
                    "Index": next_cb_offset,
                    "ArraySize": 0,
                    "Type": "Float",     # unknown — Float as the most common case
                    "RowCount": 4,       # placeholder vec4 (component count, per project convention)
                    "ColumnCount": 1,
                    "IsMatrix": False,
                })
                next_cb_offset += 16
            elif ltype in ("FShaderResourceParameter", "FShaderResourceParameterArray"):
                # Default classification as a buffer binding — the loader
                # consumer is responsible for narrowing texture vs sampler
                # vs SRV/UAV by name heuristic or cooked-binary cross-check.
                buffers.append({
                    "Name": fname,
                    "NameIndex": -1,
                    "Index": res_index,
                    "ArraySize": 0,
                })
                flat_resources.append({
                    "Index": res_index,
                    "Offset": 0,
                    "Name": fname,
                    "UbmtType": "UBMT_SRV",
                })
                res_index += 1
            elif ltype == "FRWShaderParameter":
                uavs.append({
                    "Name": fname,
                    "NameIndex": -1,
                    "Index": res_index,
                    "OriginalIndex": res_index,
                })
                flat_resources.append({
                    "Index": res_index,
                    "Offset": 0,
                    "Name": fname,
                    "UbmtType": "UBMT_UAV",
                })
                res_index += 1

        obj = {
            "Name": cname,
            "EngineVersion": engine_version,
            "EngineSource": f"{engine_relative(src_file, engine_src)}:{src_line} ({cname})",
            "LayoutHash": "0x00000000",  # not a UB layout hash — keyed by class name
            "BindingFlags": "Shader",
            "ConstantBuffer": {
                "Name": "$Globals",
                "NameIndex": -1,
                "MatrixParameters": [],
                "VectorParameters": vectors,
                "StructParameters": [],
                "Size": next_cb_offset,
                "IsPartialCB": True,
            },
            "Textures": textures,
            "Samplers": samplers,
            "Buffers": buffers,
            "UAVs": uavs,
            "Resources": flat_resources,
            "Debug": {
                "ShaderTypeClass": cname,
                "ParentClass": parent,
                "Note": (
                    "Source-derived name catalogue for the $Globals cbuffer + "
                    "direct resource bindings of this FShader subclass. The "
                    "ConstantBuffer offsets are SEQUENTIAL PLACEHOLDERS (16-byte "
                    "stride in declaration order); the cook assigns real offsets "
                    "per DXC packing of the HLSL source. Downstream loader must "
                    "reconcile by name against the cooked binary."
                ),
            },
        }
        path = out_dir / "_ShaderType" / f"{cname}_{hash_token}_MetaData.json"
        path.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        written += 1
    print(f"[gen][shader-type] wrote {written} seed file(s) under {target}")
    return written


if __name__ == "__main__":
    sys.exit(main())
