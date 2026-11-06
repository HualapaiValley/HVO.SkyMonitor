#!/usr/bin/env python3
"""Tests for ansi-to-svg.py, which draws the terminal screens in the CI artifacts and docs/images/terminal (#1317).

    python3 -m unittest discover -s tests/cli -p 'test_*.py'
"""

import importlib.util
import pathlib
import re
import unittest
import xml.etree.ElementTree as ElementTree

_spec = importlib.util.spec_from_file_location("ansi_to_svg", pathlib.Path(__file__).with_name("ansi-to-svg.py"))
ansi_to_svg = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ansi_to_svg)

SVG = "{http://www.w3.org/2000/svg}"


def draw(screen):
    """The screen drawn as SVG, parsed: a screen the converter cannot draw as valid XML fails here."""
    return ElementTree.fromstring(ansi_to_svg.render(ansi_to_svg.parse(screen), "test"))


def texts(svg):
    return [element.text for element in svg.iter(f"{SVG}text")][1:]  # the first is the window title


def fills(svg, tag):
    return {element.get("fill") for element in svg.iter(f"{SVG}{tag}")}


class AnsiToSvgTests(unittest.TestCase):
    def test_a_combining_mark_stays_with_its_letter(self):
        svg = draw("Name: José  Role: admin\n")
        self.assertIn("Name: José", texts(svg))

    def test_a_combining_mark_after_a_wide_character(self):
        svg = draw("東́ x\n")
        self.assertIn("東́", texts(svg))

    def test_wide_combining_marks_do_not_widen_narrow_clusters(self):
        for cluster in ("a゙", "ｶ゙"):
            with self.subTest(cluster=cluster):
                rows = ansi_to_svg.parse(cluster + "!")
                self.assertEqual([cluster, "!"], [cell and cell[0] for cell in rows[0]])
                svg = draw(cluster + "\x1b[31m!")
                following = next(element for element in svg.iter(f"{SVG}text") if element.text == "!")
                self.assertEqual(ansi_to_svg.PADDING + ansi_to_svg.CELL_WIDTH, float(following.get("x")))

    def test_class_zero_extenders_do_not_advance_the_column(self):
        # Mn (including a wide mark), Me and Mc can have combining class zero. Supplementary variation selectors
        # and emoji skin tones are also zero-width extenders in Terminal.Gui's Wcwidth 4.0.1 tables.
        for mark in ("\u034f", "\u20dd", "\u0903", "\U00016fe4", "\U000e0100", "\U0001f3fd"):
            with self.subTest(mark=f"U+{ord(mark):04X}"):
                cluster = "a" + mark
                self.assertFalse(ansi_to_svg.is_wide(cluster))
                rows = ansi_to_svg.parse(cluster + "!")
                self.assertEqual([cluster, "!"], [cell and cell[0] for cell in rows[0]])
                svg = draw(cluster + "\x1b[31m!")
                following = next(element for element in svg.iter(f"{SVG}text") if element.text == "!")
                self.assertEqual(ansi_to_svg.PADDING + ansi_to_svg.CELL_WIDTH, float(following.get("x")))

    def test_escapes_that_draw_nothing_are_left_out(self):
        screen = "\x1b=\x1b>a\x1b7b\x1b8\x1b(Bc\x1b[?25l\x1b[2Jd\x07\x01\x7fe\x1b[12\n"
        svg = draw(screen)
        self.assertEqual(["abcde"], texts(svg))

    def test_a_hyperlink_keeps_its_text(self):
        svg = draw("┌┤\x1b]8;;http://127.0.0.1:15196/\x1b\\HVO roof\x1b]8;;\x1b\\├┐\n")
        self.assertEqual(["HVO roof"], texts(svg))

    def test_colours_out_of_range_are_clamped(self):
        svg = draw("\x1b[38;5;300ma\x1b[38;2;300;0;999mb\n")
        colours = fills(svg, "text")
        self.assertIn("#eeeeee", colours)
        self.assertIn("#ff00ff", colours)
        for colour in colours | fills(svg, "rect"):
            self.assertRegex(colour, r"^#[0-9a-f]{6}$")

    def test_colon_separated_colours(self):
        svg = draw("\x1b[38:2::1:2:3ma\x1b[38:2:4:5:6mb\x1b[38:5:196mc\n")
        self.assertTrue({"#010203", "#040506", "#ff0000"} <= fills(svg, "text"))

    def test_the_colour_carries_on_to_the_next_line(self):
        # tmux writes a colour where it changes, so the second line has the first one's background.
        rows = ansi_to_svg.parse("\x1b[38;2;248;250;252m\x1b[48;2;5;7;13mfirst\nsecond\n\x1b[0mthird\n")
        self.assertEqual(("#f8fafc", "#05070d"), rows[1][0][1].colours())
        self.assertEqual((ansi_to_svg.DEFAULT_FG, ansi_to_svg.DEFAULT_BG), rows[2][0][1].colours())

    def test_the_margin_has_the_screens_background(self):
        svg = draw("\x1b[48;2;5;7;13mabc\ndef\n\x1b[48;2;255;193;7mg\n")
        screen = next(svg.iter(f"{SVG}rect"))
        self.assertEqual("#05070d", screen.get("fill"))
        # Only the cell with another background has a rectangle of its own.
        self.assertIn("#ffc107", fills(svg, "rect"))

    def test_a_plain_screen_keeps_the_terminal_background(self):
        svg = draw("plain text\n")
        self.assertEqual(ansi_to_svg.DEFAULT_BG, next(svg.iter(f"{SVG}rect")).get("fill"))

    def test_wide_characters_take_two_cells(self):
        rows = ansi_to_svg.parse("東京!\n")
        self.assertEqual(5, len(rows[0]))
        self.assertIsNone(rows[0][1])

    def test_emoji_clusters_take_two_cells_as_terminal_gui_draws_them(self):
        # A flag's two regional indicators, a ZWJ family, an emoji with a skin tone, and a ZWJ sequence that starts with
        # a narrow character are each one grapheme in two cells (HvoUnicodeTests), so what follows keeps its column.
        for screen, cluster in (("\U0001F1FA\U0001F1F8!", "\U0001F1FA\U0001F1F8"),
                                ("\U0001F468\u200d\U0001F469\u200d\U0001F467!", "\U0001F468\u200d\U0001F469\u200d\U0001F467"),
                                ("\U0001F44D\U0001F3FD!", "\U0001F44D\U0001F3FD"),
                                ("\U0001F3F3\ufe0f\u200d\U0001F308!", "\U0001F3F3\ufe0f\u200d\U0001F308")):
            with self.subTest(cluster=cluster):
                rows = ansi_to_svg.parse(screen + "\n")
                self.assertEqual([cluster, None, "!"], [cell and cell[0] for cell in rows[0]])

    def test_two_flags_are_two_clusters(self):
        rows = ansi_to_svg.parse("\U0001F1FA\U0001F1F8\U0001F1EF\U0001F1F5\n")
        self.assertEqual(["\U0001F1FA\U0001F1F8", None, "\U0001F1EF\U0001F1F5", None], [cell and cell[0] for cell in rows[0]])

    def test_markup_in_text_is_escaped(self):
        svg = draw("<script> & \"x\"\n")
        self.assertTrue(any(re.search(r"<script> & \"x\"", text) for text in texts(svg)))


if __name__ == "__main__":
    unittest.main()
