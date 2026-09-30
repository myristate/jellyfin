"""Render the Finly logo files from the SVG masters in this folder.

Usage: python render.py [preview]

Needs Google Chrome (to draw the SVGs) and Pillow. Writes everything to ./out, which the web and Android
builds copy from. Change the name or the artwork here and re-run to rebrand every part.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import time

from PIL import Image

NAME = 'Finly'
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'out')

# Wordmark colours: light text for dark backgrounds, dark text for light backgrounds
TEXT_LIGHT = '#f2f4f8'
TEXT_DARK = '#1b2231'
WORDMARK_FONT = "'Segoe UI Black', 'Segoe UI', Arial, sans-serif"


def screenshot(html, width, height, path):
    """Draw an HTML snippet with a transparent background at an exact pixel size."""
    with tempfile.NamedTemporaryFile('w', suffix='.html', dir=HERE, delete=False, encoding='utf-8') as f:
        f.write('<html><head><meta charset="utf-8"></head>'
                '<body style="margin:0;background:transparent;overflow:hidden">' + html + '</body></html>')
        page = f.name
    # Chrome's launcher can return before the screenshot is written, so capture to a scratch file of our own and
    # wait for it to settle; a late write can then never overwrite the finished image.
    workdir = tempfile.mkdtemp(prefix='finly-render-')
    try:
        for attempt in range(3):
            shot = os.path.join(workdir, f'shot{attempt}.png')
            subprocess.Popen(
                [CHROME, '--headless=new', '--disable-gpu', '--user-data-dir=' + os.path.join(workdir, f'profile{attempt}'),
                 '--no-first-run', '--hide-scrollbars', '--force-device-scale-factor=1',
                 '--default-background-color=00000000', f'--window-size={max(width, 800)},{max(height, 600)}',
                 '--screenshot=' + shot, 'file:///' + page.replace(os.sep, '/')],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if wait_for_file(shot, 60):
                break
        else:
            raise RuntimeError('Chrome did not render ' + path)
        img = Image.open(shot).convert('RGBA')
        img.load()
    finally:
        os.remove(page)
        shutil.rmtree(workdir, ignore_errors=True)
    img = img.crop((0, 0, width, height))
    img.save(path)
    return img


def wait_for_file(path, timeout):
    """Wait until a file exists and its size has stopped changing."""
    deadline = time.time() + timeout
    last = -1
    while time.time() < deadline:
        size = os.path.getsize(path) if os.path.exists(path) else -1
        if size > 0 and size == last:
            return True
        last = size
        time.sleep(0.5)
    return False


def mark_html(size, pad=0):
    return (f'<div style="width:{size}px;height:{size}px;padding:{pad}px;box-sizing:border-box">'
            f'<img src="mark.svg" style="width:100%;height:100%;display:block"></div>')


def banner_html(width, height, text_colour):
    mark = int(height * 0.92)
    return (f'<div style="width:{width}px;height:{height}px;display:flex;align-items:center;gap:{int(height * 0.08)}px">'
            f'<img src="mark.svg" style="width:{mark}px;height:{mark}px;flex:none">'
            f'<span style="font-family:{WORDMARK_FONT};font-weight:900;font-size:{int(height * 0.74)}px;'
            f'line-height:1;letter-spacing:-0.02em;color:{text_colour}">{NAME}</span></div>')


def tv_banner_html(width, height):
    # Android TV home screen banner: full-bleed tile with mark and name
    mark = int(height * 0.62)
    return (f'<div style="width:{width}px;height:{height}px;display:flex;align-items:center;justify-content:center;'
            f'gap:{int(height * 0.05)}px;background:linear-gradient(135deg,#232a3d 0%,#3d2a57 60%,#6b3f7e 100%)">'
            f'<img src="mark.svg" style="width:{mark}px;height:{mark}px;flex:none">'
            f'<span style="font-family:{WORDMARK_FONT};font-weight:900;font-size:{int(height * 0.36)}px;'
            f'line-height:1;letter-spacing:-0.02em;color:{TEXT_LIGHT}">{NAME}</span></div>')


def square_icon_html(size):
    # App icon on a dark rounded tile, for places that need an opaque square
    return (f'<div style="width:{size}px;height:{size}px;border-radius:{int(size * 0.22)}px;'
            f'background:linear-gradient(135deg,#232a3d 0%,#3d2a57 70%,#6b3f7e 100%);display:flex;'
            f'align-items:center;justify-content:center">'
            f'<img src="mark.svg" style="width:{int(size * 0.8)}px;height:{int(size * 0.8)}px"></div>')


def main():
    os.makedirs(os.path.join(OUT, 'favicons'), exist_ok=True)

    if len(sys.argv) > 1 and sys.argv[1] == 'preview':
        mark = screenshot(mark_html(512), 512, 512, os.path.join(OUT, 'preview-mark.png'))
        sheet = Image.new('RGBA', (1400, 900), (238, 240, 244, 255))
        dark = Image.new('RGBA', (1400, 450), (20, 24, 33, 255))
        sheet.paste(dark, (0, 450))
        sheet.alpha_composite(mark.resize((320, 320)), (40, 60))
        sheet.alpha_composite(screenshot(banner_html(900, 180, TEXT_DARK), 900, 180, os.path.join(OUT, 'p1.png')), (420, 130))
        sheet.alpha_composite(screenshot(banner_html(900, 180, TEXT_LIGHT), 900, 180, os.path.join(OUT, 'p2.png')), (420, 580))
        sheet.alpha_composite(screenshot(tv_banner_html(320, 180), 320, 180, os.path.join(OUT, 'p3.png')), (40, 600))
        sheet.save(os.path.join(OUT, 'preview.png'))
        for f in ('p1.png', 'p2.png', 'p3.png'):
            os.remove(os.path.join(OUT, f))
        print('preview written')
        return

    # Web client
    icon = screenshot(mark_html(512), 512, 512, os.path.join(OUT, 'icon-transparent.png'))
    screenshot(banner_html(1000, 240, TEXT_LIGHT), 1000, 240, os.path.join(OUT, 'banner-light.png'))
    screenshot(banner_html(1000, 240, TEXT_DARK), 1000, 240, os.path.join(OUT, 'banner-dark.png'))
    tile = screenshot(square_icon_html(512), 512, 512, os.path.join(OUT, 'favicons', 'touchicon512.png'))
    for size in (72, 114, 144, 180):
        name = 'touchicon.png' if size == 180 else f'touchicon{size}.png'
        tile.resize((size, size), Image.LANCZOS).save(os.path.join(OUT, 'favicons', name))
    icon.save(os.path.join(OUT, 'favicons', 'favicon.ico'), sizes=[(16, 16), (32, 32), (48, 48), (64, 64)])

    # Android TV app
    screenshot(tv_banner_html(320, 180), 320, 180, os.path.join(OUT, 'tv-banner.png'))
    screenshot(tv_banner_html(640, 360), 640, 360, os.path.join(OUT, 'tv-banner@2x.png'))
    tile.resize((432, 432), Image.LANCZOS).save(os.path.join(OUT, 'app-icon-432.png'))
    print('assets written to', OUT)


if __name__ == '__main__':
    main()
