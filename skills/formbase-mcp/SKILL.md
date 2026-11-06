---
name: formbase-mcp
description: How to read a Formbase host through its read-only MCP door - check that a projection can be trusted before reading records, read the raw documents behind them, and report what was skipped. Use when an agent has the Formbase MCP tools (describe_form_type, query_records, projection_status, list_documents, get_document) and has to answer a question from filed forms.
---

# Reading Formbase through the MCP door

Formbase keeps every filed form as a **document** (raw, exactly as accepted) and, once a form type has a declaration, a
**projection** of those documents into queryable records. The door is read-only.

## Work in this order

1. `describe_form_type` - the declaration in force: fields, types, relations. "No declaration" is an answer, not an error:
   then there are documents but no records yet.
2. `projection_status` **before you trust an empty or short result.** It says `notProjected`, `projected`, `stale` or
   `unverified` - which a record read alone cannot tell from "no data". Only `projected` means the records are current.
3. `query_records` - projected records. Filters are **equality only** (`column:value`). Read these two fields of the answer
   every time: `stale` (the projection is behind the documents) and `skippedDocuments` (documents that could not be projected).
   Rows are then never silently fewer than the documents - say it when either is not zero or false.
4. `list_documents` - the raw stream, oldest first, from the `after` watermark. It works whether or not the form type has a
   declaration, so **fields nobody declared are readable here**. Continue from the last watermark you received until it
   equals `rawHead`; a single page is not the stream.
5. `get_document` - one document by id: the source of truth, with its body, when it was appended and the record key it
   corrects (if any). Cite the document id when you state a value.

## Read the answers

- A record is the *current* state of a thing; documents are the history. A document that corrects a record (it names a record
  key) supersedes the earlier ones for that key. Do not report a superseded value as current.
- References between form types are declared; `referenceSources` on `query_records` says which record a reference was read from.
- The door cannot tell people apart: it holds no per-person permissions.

## For whoever configures the agent

An access key **opens the whole host** - every document, record and the settings - and the door refuses to start on a host with
no key. Give an agent a key meant for it and rotate it like any other. See "What the host trusts, and what leaves it" in `docs/API.md`.
