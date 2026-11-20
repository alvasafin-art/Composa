# Text

Text lives on its own layer and stays editable: its wording, font, size and color can be changed at any time, and scaling the layer lays the text out again rather than stretching pixels.

## Adding text

With the Type tool (T), click on the canvas for a single line of text; the click sets the baseline, so the letters rise from where you clicked. Drag a box instead for a paragraph that wraps inside it. Type, then press Ctrl+Enter or click Done in the options bar to finish, or Escape or Cancel to give up. Text left empty is discarded. New text takes the foreground color and the style you used last.

## Editing text

Click existing text with the Type tool, double-click it with the Move tool, double-click the text layer's thumbnail in the Layers panel, or choose Layer > Edit Text. While editing, the box has handles to resize it (a single line becomes a box when you resize it), and a plus sign in the bottom-right handle shows that some text does not fit.

Keys while typing: Enter starts a new line; Tab inserts a tab; the arrow keys move, with Ctrl a word at a time and with Shift extending the selection; Home and End go to the start and end of the line, with Ctrl to the start and end of the text; Ctrl+A selects everything; Backspace and Delete remove, with Ctrl a word at a time; Ctrl+Z, Ctrl+Shift+Z or Ctrl+Y undo and redo within the text; Ctrl+C, Ctrl+X and Ctrl+V copy, cut and paste plain text; Alt with Left and Right changes the tracking and Alt with Up and Down the leading, by one, or ten with Shift. A double-click selects a word, and Shift-click extends the selection.

## Text properties

Select a text layer on the canvas with Move or select its row in Layers. **Window > Properties** shows its typeface, style, size, color, alignment and spacing. With Properties hidden the Type tool retains its quick controls. Within an open text edit, font family, style, size and color apply to the selected characters. Paragraph settings apply to the text block.

Open the font family list and move the pointer or use the arrow keys to preview each family on the canvas. Enter or a click accepts the family. Escape or dismissal discards the sample. Browsing does not modify the project, undo history or export. The closest installed weight and slant are used for each selected run. Type a family name and press Enter to select it. A warning beside a family indicates that it lacks glyphs for characters in the current text.

Drag horizontally inside a numeric field to adjust its value on the canvas. A click still allows typing. Enter starts a paragraph. Shift+Enter inserts a line break within the current paragraph and keeps its list number and spacing. Expanded type sections stay open when switching layers.

The spacing fields show line height (zero means Auto) and letter spacing. Click the unit beside either spacing field to switch between pixels and percent of font size. Underline and strikethrough can be combined. **Paragraph** contains alignment in the text box, lists, wrapping, ellipsis, vertical trim, hanging punctuation, first-line indent and spacing between paragraphs. **Type details** includes non-destructive case transforms and OpenType options for capitals and numerals. Available features depend on the font. Variable fonts expose their installed axes in **Variable font**.

A change in the type controls applies to the text you are typing, or to the active text layer when none is being typed, or to the next text you add when no text layer is active. A run of changes on one layer undoes as a single step.

## Photoshop export

PSD text supports editable font runs, underline, strikethrough, leading, tracking, justification, baseline position, paragraph gap and first-line indent. Photoshop stores tracking in integer thousandths of an em. Lowercase and title case are exported as the displayed letters. For a layout that uses features such as lists, variable axes or ellipsis, the PSD contains the rendered layer and its text in a hidden **Editable text** folder. The Composa project retains the original settings.

## Letters in their own colors

While typing, select some of the text and pick a color from the bar's swatch or the foreground swatch, and only those letters take it. With nothing selected, or on a text layer that is not open for typing, the color goes on all of the text. New letters take the color of the letter before them, and the swatch shows the color at the caret. Fill with Foreground Color or Fill with Background Color recolors the whole text and keeps it editable.

## Letters in their own fonts

Font family and style work the same way: while typing, select some of the text and choose a family or style, and only those letters change. With nothing selected the change goes on all of the text. Changing the family chooses its closest available weight, width and slant for each letter. A selection mixing families or styles shows (Multiple) in the corresponding menu. Choose one family before choosing a shared style for letters in different families. New letters take the face of the letter before them.

Styles come from the installed font rather than synthesized Bold and Italic buttons. Exact weights, widths and slants are saved in project format version 8 and in editable PSD text. Older projects' Bold and Italic settings remain supported.
