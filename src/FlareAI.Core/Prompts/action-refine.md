A reviewer proposed a change to one passage of a technical document. The author wants it refined.

Passage (plain text of the paragraph):
{{CONTEXT}}

The text to change (exact, inside the passage):
{{EXCERPT}}

The reviewer's proposal and reason:
{{PROPOSAL}}

The author's instruction:
{{INSTRUCTION}}

Follow the author's instruction while still applying the style guide. The replacement must read correctly when swapped
in for exactly the text to change, with the same tense and grammar as the rest of the passage. Never change a modal verb (must, should, may, might, shall) or add one: it sets an obligation
only the author can change. Never change or drop code, API and product terms (identifiers, field, method and parameter
names, part numbers): copy them exactly. Never add a fact the text doesn't state.

If the instruction doesn't apply to this text (for example it names words the text doesn't contain), set applies to
false and return the reviewer's proposal unchanged. If the instruction means
the text should stay as it is, return the text to change unchanged.

When a replacement applies a rule that names what to use instead, use only words that rule names or that the text or glossary already uses. Never invent another term. When the right word depends on context, set replacement to null and say in why that the author should choose the role.
