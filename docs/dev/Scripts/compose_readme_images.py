"""Composes the README's clip box images from the renders of the dev project's ReadmeCaptures editor script.

Render first (Editor closed, no -nographics):
    Unity.exe -batchmode -projectPath Fuluma -executeMethod ReadmeCaptures.CaptureFromCommandLine
        -readmeCaptureConfig Builds/ReadmeCaptures/captures.json
then run this from the repository root:
    python docs/dev/Scripts/compose_readme_images.py
It reads Fuluma/Builds/ReadmeCaptures/final/*.png and writes resources/images/ClipBoxRoomComparison.jpg,
ClipBoxSetups.png and ScatteringPhase.jpg, 1920 pixels wide.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

RENDERS = Path("Fuluma/Builds/ReadmeCaptures/final")
OUTPUT = Path("resources/images")
WIDTH = 1920
BAND = (38, 38, 38)
TITLE = (240, 240, 240)
SUBTITLE = (190, 190, 190)
TAG_BACKGROUND = (0, 0, 0, 170)


def font(name, size):
    return ImageFont.truetype(str(Path("C:/Windows/Fonts") / name), size)


def panel(name, size):
    return Image.open(RENDERS / f"{name}.png").convert("RGB").resize(size, Image.Resampling.LANCZOS)


def header(columns, column_width, height):
    """A band with a centered title and subtitle per column."""
    band = Image.new("RGB", (column_width * len(columns), height), BAND)
    draw = ImageDraw.Draw(band)
    title_font = font("segoeuib.ttf", 40)
    subtitle_font = font("segoeui.ttf", 28)
    for i, (title, subtitle) in enumerate(columns):
        center = column_width * i + column_width // 2
        draw.text((center, height * 0.36), title, font=title_font, fill=TITLE, anchor="mm")
        draw.text((center, height * 0.72), subtitle, font=subtitle_font, fill=SUBTITLE, anchor="mm")
    return band


def tag(image, text):
    """A small dark label in the panel's top-left corner."""
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    tag_font = font("segoeuib.ttf", 28)
    left, top, right, bottom = draw.textbbox((0, 0), text, font=tag_font)
    padding = 12
    draw.rounded_rectangle((14, 14, 14 + right - left + 2 * padding, 14 + bottom - top + 2 * padding), 8,
                           fill=TAG_BACKGROUND)
    draw.text((14 + padding - left, 14 + padding - top), text, font=tag_font, fill=TITLE)
    return Image.alpha_composite(image.convert("RGBA"), overlay).convert("RGB")


def grid(columns, rows, panel_size, header_height):
    """columns: (title, subtitle, [render per row]); rows: the row tags."""
    column_width, row_height = panel_size
    image = Image.new("RGB", (column_width * len(columns), header_height + row_height * len(rows)), BAND)
    image.paste(header([(c[0], c[1]) for c in columns], column_width, header_height), (0, 0))
    for c, (_, _, renders) in enumerate(columns):
        for r, render in enumerate(renders):
            image.paste(tag(panel(render, panel_size), rows[r]), (c * column_width, header_height + r * row_height))
    # Thin separators between panels.
    draw = ImageDraw.Draw(image)
    for c in range(1, len(columns)):
        draw.line((c * column_width, header_height, c * column_width, image.height), fill=BAND, width=4)
    for r in range(1, len(rows)):
        y = header_height + r * row_height
        draw.line((0, y, image.width, y), fill=BAND, width=4)
    return image


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)

    room = grid(
        [("Without a clip box", "The glow leaks through the walls.", ["room-inside", "room-outside"]),
         ("With a Keep clip box", "The glow stays inside the room.", ["room-inside-box", "room-outside-box"])],
        ["Inside", "Outside"], (WIDTH // 2, WIDTH // 2 * 9 // 16), 130)
    room.save(OUTPUT / "ClipBoxRoomComparison.jpg", quality=88, optimize=True, progressive=True)

    setups = grid(
        [("A room", "One Keep box", ["setup-room", "setup-room-box"]),
         ("A room with a pillar", "Keep + Subtract", ["setup-pillar", "setup-pillar-box"]),
         ("An L-shaped room", "Two Keep boxes", ["setup-lshape", "setup-lshape-box"])],
        ["Without boxes", "With boxes"], (WIDTH // 3, WIDTH // 3 * 9 // 16), 130)
    setups.save(OUTPUT / "ClipBoxSetups.png", optimize=True)

    scattering = grid(
        [("Isotropic", "Equally bright from every side", ["scattering-isotropic-side", "scattering-isotropic-toward"]),
         ("Henyey-Greenstein", "Anisotropy 0.7: faint from the side, bright toward the lamp",
          ["scattering-hg-side", "scattering-hg-toward"])],
        ["From the side", "Looking toward the lamp"], (WIDTH // 2, WIDTH // 2 * 9 // 16), 130)
    scattering.save(OUTPUT / "ScatteringPhase.jpg", quality=88, optimize=True, progressive=True)

    for name in ("ClipBoxRoomComparison.jpg", "ClipBoxSetups.png", "ScatteringPhase.jpg"):
        path = OUTPUT / name
        print(f"{path}: {Image.open(path).size}, {path.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
