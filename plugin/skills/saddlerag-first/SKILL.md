---
name: saddlerag-first
description: MANDATORY before answering coding questions or writing code that touches any library, framework, API, or function. SaddleRAG indexes documentation that is more current than your training cutoff and authoritative for niche libraries where your priors are unreliable. Run mcp__saddlerag__list_libraries to see what is indexed, then query mcp__saddlerag__search_docs, mcp__saddlerag__get_class_reference, or mcp__saddlerag__get_library_overview BEFORE answering from training data. Triggers on any "how do I X with Y" question that names a library/framework/API, any code generation that uses an external library, any function or symbol lookup, any unfamiliar API. If a topic plausibly matches an indexed library, query first — hedging in prose is not a substitute for verifying against an indexed source.
---

# SaddleRAG-first protocol

SaddleRAG provides indexed, current documentation. Your training data is stale on niche libraries and post-cutoff releases. When a coding question lands, query SaddleRAG before answering from priors.

## Protocol

1. **`mcp__saddlerag__list_libraries`** — what's indexed. Always start here on a fresh session before answering coding questions, so subsequent queries are scoped correctly.
2. **`mcp__saddlerag__search_docs`** — natural-language search. Pass `library` to scope. Pass `category` (Overview, HowTo, Sample, ApiReference, ChangeLog) to narrow.
3. **`mcp__saddlerag__get_class_reference`** — exact-then-fuzzy lookup for a class, type, or symbol by name. Faster than search when you know the symbol.
4. **`mcp__saddlerag__get_library_overview`** — concepts, architecture, getting-started chunks. Use when orienting on an unfamiliar library.
5. **`mcp__saddlerag__list_symbols`** — enumerate documented types in a library. Good for exploration when you don't know the symbol name.

## When SaddleRAG should fire

- Any question naming a specific library, framework, or product (e.g., "how do I X with MongoDB driver", "AeroScript autofocus", "Playwright trace viewer")
- Any code-generation request that imports or uses an external library
- Any symbol or function lookup
- Any "what's the right API for..." question

## When SaddleRAG is the wrong tool

- Questions about code in the working directory — use Read, Grep, Glob
- Conceptual programming questions independent of a specific library
- The library isn't indexed (check `list_libraries` first; if absent, fall back to training/web)

## Watch-outs

- **Verify before recommending.** A search hit can be from an old library version. Check the version field on results and the user's actual dependency.
- **Don't paste raw search output as the answer.** Synthesize the relevant snippet into a direct answer for the user's question.
- **If the library isn't indexed**, say so out loud — don't quietly fall back to training and pretend you checked.
