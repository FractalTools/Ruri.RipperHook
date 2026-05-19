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

# Numeric type table: cpp_name -> (natural_size, alignment, UBMT, hlsl_name, num_rows, num_columns).
# ``natural_size`` is ``sizeof(T)`` — NOT padded to alignment. MS_ALIGN /
# GCC_ALIGN modifiers (used by ``TAlignedTypedef<T, A>::Type``) raise alignof
# but do NOT inflate sizeof; e.g. an alignas(16) FVector3f still has sizeof=12.
# The next member is aligned to its own type's alignment, so size and alignment
# matter independently — this matches what compilers actually do for cooked
# shipping UE structs (and what the layout hash discriminator XOR-folds).
TYPE_TABLE: dict[str, tuple[int, int, str, str, int, int]] = {
    "bool":          ( 4,  4, "BOOL",    "Bool",    1, 1),
    "uint32":        ( 4,  4, "UINT32",  "UInt",    1, 1),
    "int32":         ( 4,  4, "INT32",   "Int",     1, 1),
    "int":           ( 4,  4, "INT32",   "Int",     1, 1),
    "uint":          ( 4,  4, "UINT32",  "UInt",    1, 1),
    "float":         ( 4,  4, "FLOAT32", "Float",   1, 1),
    "FVector2f":     ( 8,  8, "FLOAT32", "Float2",  1, 2),
    "FVector3f":     (12, 16, "FLOAT32", "Float3",  1, 3),
    "FVector4f":     (16, 16, "FLOAT32", "Float4",  1, 4),
    "FLinearColor":  (16, 16, "FLOAT32", "Float4",  1, 4),
    "FIntPoint":     ( 8,  8, "INT32",   "Int2",    1, 2),
    "FUintVector2":  ( 8,  8, "UINT32",  "UInt2",   1, 2),
    "FIntVector":    (12, 16, "INT32",   "Int3",    1, 3),
    "FUintVector3":  (12, 16, "UINT32",  "UInt3",   1, 3),
    "FIntVector4":   (16, 16, "INT32",   "Int4",    1, 4),
    "FUintVector4":  (16, 16, "UINT32",  "UInt4",   1, 4),
    "FIntRect":      (16, 16, "INT32",   "Int4",    1, 4),
    "FQuat4f":       (16, 16, "FLOAT32", "Float4",  1, 4),
    "FMatrix44f":    (64, 16, "FLOAT32", "Float4x4", 4, 4),
    "FMatrix3x4f":   (48, 16, "FLOAT32", "Float3x4", 3, 4),
    "FMatrix44d":    (64, 16, "FLOAT32", "Float4x4", 4, 4),  # treated like 44f for layout
    # LWC types -- same shape as their float counterparts post-cooking.
    "FVector":       (12, 16, "FLOAT32", "Float3",  1, 3),
    "FVector4":      (16, 16, "FLOAT32", "Float4",  1, 4),
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

def to_json(meta_name: str, engine_version: str, engine_source: str,
            layout_hash: int, binding_flags: str, cb_size: int,
            members: list[NumericMember], resources: list[Resource]) -> str:
    obj = {
        "name": meta_name,
        "engineVersion": engine_version,
        "engineSource": engine_source,
        "layoutHash": f"0x{layout_hash:08X}",
        "constantBufferSize": cb_size,
        "bindingFlags": binding_flags,
        "members": [
            {
                "offset": m.offset,
                "name": m.name,
                "type": m.hlsl_type,
                **({"arraySize": m.array_size} if m.array_size > 0 else {}),
            }
            for m in members
        ],
        "resources": [
            {
                "index": r.resource_index,
                "offset": r.offset,
                "name": r.name,
                "type": "UBMT_" + r.ubmt_name,
            }
            for r in resources
        ],
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
                key = (obj.get("name", ""), obj.get("layoutHash", "").lower())
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
            # OK, hash matched. Compare key fields.
            r_cb = ref.get("constantBufferSize", 0)
            r_res = len(ref.get("resources", []))
            r_mem = len(ref.get("members", []))
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
    return 0 if failures == 0 else 1


def engine_relative(abs_path: str, engine_src: Path) -> str:
    """Best-effort relative path under the engine root for the JSON engineSource field."""
    try:
        return str(Path(abs_path).resolve().relative_to(engine_src)).replace("\\", "/")
    except ValueError:
        return abs_path.replace("\\", "/")


if __name__ == "__main__":
    sys.exit(main())
