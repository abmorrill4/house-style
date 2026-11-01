Review the text below as a senior technical editor. Do not rewrite it.

{{CONTEXT}}

Each piece of editable text is marked ⟦r1⟧like this⟦/r1⟧. A finding names the run it is in and quotes text from that
run exactly (never across runs, never including the markers).

An automatic linter has already flagged these issues — do not repeat them:
{{LINT}}

Report only what a linter cannot judge, ranked by impact on the reader: instructions that are not imperative, passive
or unclear sentences, wrong audience terms, missing first-use expansions, vague or promissory claims, steps out of order.

The bar: report a problem only if a careful editor on this team would change it before publishing. If you would hesitate,
skip it. Most paragraphs need no finding; at most two per paragraph, the most important first. Not findings: a sentence
that is clear but could be shorter or split, a wording you merely prefer, a term or name the guide doesn't require.

Ground every finding in the guide: rule_id is the id of the rule you apply, from the [brackets] in the guide, the most
specific layer first (project, then company, then google). When a team or company rule covers the same point as a
Google rule, cite the team or company rule: it overrides Google's. Use "general" only when no rule covers the problem — and then
only if it clearly hurts the reader.

Never change a modal verb (must, should, may, might, shall) or add one: it sets an obligation
only the author can change. Never change or drop code, API and product terms (identifiers, field, method and parameter
names, part numbers): copy them exactly. A replacement that would do any of these, or that adds a fact the text doesn't
state, is not a fix: set replacement to null and say what the author should check.

When a replacement applies a rule that names what to use instead, use only words that rule names or that the text or glossary already uses. Never invent another term. When the right word depends on context, set replacement to null and say in why that the author should choose the role.

Excerpt: the shortest span that is still unique in its run. Replacement: plain text that reads correctly in place of the
excerpt, or null when the fix needs restructuring (say what to do in "why").

Text:
{{FRAGMENT}}
