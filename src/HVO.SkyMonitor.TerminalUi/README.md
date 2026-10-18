# HVO.SkyMonitor.TerminalUi

What SkyMonitor's terminal programs share: HVO Dark for Terminal.Gui 2.5.0. The installer's wizard
(`hvo-sky-install`, #1317) uses it now, and `hvo-sky ui` builds on it. It follows RoofController's
`HVO.RoofControllerV4.TerminalUi` in structure (one project, Terminal.Gui only, used by every terminal program), and in
look the terminal prototype of RoofController #158, which redraws every RoofController interface in SkyMonitor
CameraAgent's clean HVO Dark styling (`docs/prototypes/roofcontroller-terminal.html` and `docs/prototypes/README.md`
in HVO.RoofController at `9cc8f2bbc677f41a9e30217a9881ded0c47860a9`).

The project references Terminal.Gui and nothing else. The architecture test `TerminalUiBoundaryIsNarrow` keeps it so.

## The design

As the prototype draws it:

- **Character cells only.** Every mark is a whole cell: no shadows, gradients or rounded app panels.
- **The screen.** A bar at the top with the program's name in bold capitals and a badge on the right
  (`HvoHeaderBar`), a muted context line under it, a notice band when there is something to say (`HvoNoticeBar`), the
  content in titled panels (`HvoPanel`), a row of tabs or steps (`HvoTabBar`), and the keys at the foot (`HvoKeyBar`).
- **Panels.** A frame in the border colour on the surface, the title in capitals and the accent colour between the
  top corners, and the content one cell inside.
- **Fields.** A muted label in a column, and the value beside it, aligned with the others.
- **Actions.** Bracketed, `[ Next ]`, the primary one on the strong accent (`HvoTheme.CreateButton`).
- **Bounded content.** Text wraps inside its box, a column's continuation lines indented under the column
  (`HvoTextLines`); when there is more than fits, the last row says which lines show and which keys scroll.
- **Sizes.** Usable at 80 x 24. At 120 x 32 there is room for more: the wizard shows every answer so far beside the page.
- **Keyboard.** Tab moves, Enter does what the primary action says, and Esc closes (the wizard: cancels).
- **Never colour alone.** Every state also has a word or a mark: a notice starts with `[ERROR]`, `[WARNING]`, `[OK]` or
  `[INFO]`, a choice is `(*)` or `( )`, a yes-or-no question `[x]` or `[ ]`, the current tab is in brackets, and an
  answer the command line gave is marked.

## Token contract

`HvoDarkPalette` maps the CameraAgent stylesheet, `src/HVO.SkyMonitor.CameraAgent/wwwroot/css/themes/hvo-dark.css`,
onto the roles the prototype uses. A token the stylesheet gives with transparency (the notice bands) is the colour it
draws over the page. The ANSI-16 column is the colour drawn on a terminal without true colour, named as Terminal.Gui
names the 16 colours (`Gray` is the standard white, 7; `DarkGray` is bright black, 8; `White` is bright white, 15).
`HvoDarkPaletteTests` fails when the stylesheet or this table disagrees with `HvoDarkPalette`. RoofController #158
asks for the same kind of contract, and can mirror these role names.

| Role | CSS token | Value | ANSI-16 | Used for |
| --- | --- | --- | --- | --- |
| background | `--hvo-body-bg` | `#05070d` | Black | The screen behind everything else |
| surface | `--hvo-surface` | `#0d1420` | Black | Panels and the bars at the top and foot |
| surface-raised | `--hvo-surface-elevated` | `#111c2a` | Black | Buttons and input fields |
| border | `--hvo-border-strong` | `#3a4a60` | DarkGray | Panel frames and rules |
| text | `--hvo-body-color` | `#f7f9fc` | White | Text and values |
| muted | `--hvo-muted` | `#98a7bb` | Gray | Field labels, hints, the key bar's names |
| accent | `--hvo-accent` | `#58a6ff` | BrightCyan | Panel titles, keys, the focused button |
| focus | `--hvo-accent-strong` | `#2563eb` | Blue | The selected item and the primary action |
| success | `--hvo-success-strong` | `#3fb950` | BrightGreen | A check that passed; a change made |
| warning | `--hvo-warning-strong` | `#d29922` | BrightYellow | Something to read before going on |
| danger | `--hvo-danger-strong` | `#f85149` | BrightRed | An error, a refusal, a step that failed |
| info | `--hvo-info-strong` | `#58a6ff` | BrightCyan | Information and progress |
| success-background | `--hvo-success-bg` | `#0c3721` | Green | A success notice's band |
| success-text | `--hvo-success-fg` | `#bbf7d0` | Black | A success notice's text |
| warning-background | `--hvo-warning-bg` | `#42380f` | Yellow | A warning notice's band |
| warning-text | `--hvo-warning-fg` | `#fef9c3` | Black | A warning notice's text |
| danger-background | `--hvo-danger-bg` | `#422226` | Red | An error notice's band |
| danger-text | `--hvo-danger-fg` | `#fecaca` | White | An error notice's text |
| info-background | `--hvo-info-bg` | `#123548` | Cyan | An information notice's band |
| info-text | `--hvo-info-fg` | `#58a6ff` | Black | An information notice's text |

The prototype's own sample colours for its status text (`#8fe49c`, `#ff9b95`, `#e6bd62`) are not stylesheet tokens;
the stylesheet's `--hvo-*-strong` tokens stand in for them, so the terminal and the web pages cannot drift apart.

## Terminals: colours and frames

`TerminalCapabilities.Detect` decides once, from the environment, how a program draws, and `HvoTheme.For` gives the
theme for it:

| Mode | When | How it draws |
| --- | --- | --- |
| True colour | `COLORTERM=truecolor` or `24bit`, or a `TERM` naming 256 colours or direct colour | The Value column |
| ANSI 16 | Any other `TERM` (`linux`, `xterm`, `vt220`, `screen`), or none | The ANSI-16 column; `HvoTheme.Prepare` makes Terminal.Gui write the 16 colours as such |
| No colour | `NO_COLOR` set and not empty (https://no-color.org), or `TERM=dumb` | The terminal's own colours, with bold for titles and warnings, underline for input fields, reverse video for the focus, the selection and the primary action, and faint for what is disabled |
| ASCII frames | A locale (`LC_ALL`, else `LC_CTYPE`, else `LANG`) that is not UTF-8, or `TERM=dumb` or `vt100` | `+-+|++` in place of `┌─┐│└┘` |

`HVO_TERMINAL` overrides the detection with a comma-separated list of `truecolor`, `ansi16`, `nocolor`, `ascii` and
`unicode`; `NO_COLOR` still wins over a colour it names. Terminal.Gui's own borders, check boxes and option lists draw
glyphs that have no ASCII form, so the project draws its own (`HvoPanel`, `HvoToggle`, `HvoChoiceList`) and buttons
have Terminal.Gui's decorations turned off.

## What a program uses

| Type | What it is |
| --- | --- |
| `HvoDarkPalette`, `HvoColourToken` | The token contract above |
| `TerminalCapabilities` | Colours and frames, detected from the environment |
| `HvoTheme` | Schemes for the page, panels, fields, choices, buttons, bars, notices and text in each role; `CreateButton`, `StyleField` (the focused field on the strong accent, its label bold), `Prepare` |
| `HvoHeaderBar`, `HvoNoticeBar`, `HvoTabBar`, `HvoKeyBar` | The rows of a screen |
| `HvoPanel` | A titled panel; content goes in its `Body` |
| `HvoTextLines`, `HvoLine` | Bounded, wrapped, scrollable lines in any role |
| `HvoChoiceList`, `HvoToggle` | One of several answers, and a yes-or-no question |
| `HvoConsoleText` | The same panels and notices as plain terminal output, for what a program prints outside Terminal.Gui |
| `TerminalRenders` | Saves a screen as ANSI for the screenshots |

## Screenshots

A test draws a screen on Terminal.Gui's ANSI driver in memory and saves it with `TerminalRenders.Save` as
`NAME.ans`, in `HVO_TERMINAL_RENDERS_DIR` or the test results' `terminal-renders` folder. `tests/cli/ansi-to-svg.py`
draws each as an SVG. Development v1 CI does this in Build and Unit and keeps the SVGs in its evidence artifact, and
`scripts/docs:terminal-screenshots` refreshes `docs/images/terminal/` from a local run. The installer's renders are
named `install-*`; `hvo-sky ui`'s add theirs beside them, under their own prefix, and the script copies every render.
