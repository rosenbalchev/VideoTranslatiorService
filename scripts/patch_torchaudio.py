"""
Patches torchaudio._torchcodec.save_with_torchcodec to fall back to soundfile
when torchcodec is not installed. Run once after pip install soundfile.
"""
import os
import re
import sys


def main():
    try:
        import torchaudio
    except ImportError:
        print("torchaudio not found — skipping patch")
        return

    path = os.path.join(os.path.dirname(torchaudio.__file__), "_torchcodec.py")

    if not os.path.exists(path):
        print(f"_torchcodec.py not found at {path} — skipping patch")
        return

    with open(path, encoding="utf-8") as f:
        code = f.read()

    if "_soundfile_fallback" in code:
        print("torchaudio already patched — nothing to do")
        return

    pattern = re.compile(
        r'raise ImportError\(\s+"TorchCodec is required[^"]*"[^)]*\)\s+from e',
        re.DOTALL,
    )

    if not pattern.search(code):
        print("ERROR: Expected torchcodec error pattern not found in _torchcodec.py")
        print("       torchaudio version may have changed — patch not applied")
        sys.exit(1)

    replacement = (
        "# _soundfile_fallback\n"
        "        import soundfile as _sf\n"
        "        _d = src.numpy()\n"
        "        if _d.ndim > 1:\n"
        "            _d = _d.T\n"
        "        _sf.write(str(uri), _d, sample_rate)\n"
        "        return"
    )

    patched = pattern.sub(replacement, code)

    with open(path, "w", encoding="utf-8") as f:
        f.write(patched)

    print(f"Patched {path}")
    print("torchaudio.save() will now use soundfile when torchcodec is unavailable")


if __name__ == "__main__":
    main()
