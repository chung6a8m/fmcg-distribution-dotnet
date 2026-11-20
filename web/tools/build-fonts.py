"""Build the two UI-01 static faces from the repository's licensed Noto Sans."""
from hashlib import sha256
from pathlib import Path
from shutil import copyfile

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

WEB = Path(__file__).resolve().parents[1]
SOURCE = WEB.parent / "docs/ui-ux/reference-tests/fonts"
OUTPUT = WEB / "public/fonts"
SOURCE_HASH = "bfb7bb691513f12e734dc346c03a03f784912432d7e3fa8e56efcf906fe86b3d"
# Basic Latin through Latin Extended-B; combining marks; Latin Extended
# Additional (including Vietnamese); punctuation, currencies and common symbols.
COVERAGE = "U+0020-024F,U+0300-036F,U+1E00-1EFF,U+2000-206F,U+20A0-20CF,U+2116,U+2122,U+2190-2193,U+2212"


def build(weight):
    with TTFont(SOURCE / "NotoSans.ttf", recalcTimestamp=False) as font:
        instantiateVariableFont(font, {"wght": weight, "wdth": 100},
                                inplace=True, updateFontNames=True)
        options = subset.Options()
        options.layout_features = ["*"]
        options.name_IDs += [13, 14]  # Preserve licensing metadata.
        subsetter = subset.Subsetter(options=options)
        subsetter.populate(unicodes=subset.parse_unicodes(COVERAGE))
        subsetter.subset(font)
        font.flavor = "woff2"
        path = OUTPUT / f"noto-sans-{weight}.woff2"
        font.save(path)
        print(sha256(path.read_bytes()).hexdigest(), path.name)


if __name__ == "__main__":
    if sha256((SOURCE / "NotoSans.ttf").read_bytes()).hexdigest() != SOURCE_HASH:
        raise SystemExit("Unexpected Noto Sans source SHA-256")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    copyfile(SOURCE / "OFL.txt", OUTPUT / "OFL.txt")
    for static_weight in (400, 700):
        build(static_weight)
