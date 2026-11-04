# -*- coding: utf-8 -*-
"""Python tokenisation reference for milestone 0 (BACKEND.md §10).

Lab notebook: NOT shipped. The product is pure .NET.

Downloads the multilingual-e5-small tokenizer ONCE into backend/models/ and
tokenises the corpus. C# then reads EXACTLY that same tokenizer.json: if each
side fetched its own, the test would prove nothing.

The ids are dumped with and without special tokens. If Tokenizers.DotNet
disagrees, it has to be possible to tell "it puts the specials somewhere else"
(trivial) apart from "it segments the text differently" (fatal: embeddings
degrade silently).

Output: reference_ids.json
"""
import hashlib
import json
import pathlib

from transformers import AutoTokenizer

MODELO = "intfloat/multilingual-e5-small"
AQUI = pathlib.Path(__file__).resolve().parent
DIR_MODELO = AQUI.parent / "models" / "multilingual-e5-small"


def sha256(ruta: pathlib.Path) -> str:
    return hashlib.sha256(ruta.read_bytes()).hexdigest()


def main() -> None:
    corpus = json.loads((AQUI / "corpus.json").read_text(encoding="utf-8"))

    DIR_MODELO.mkdir(parents=True, exist_ok=True)
    tok = AutoTokenizer.from_pretrained(MODELO)
    tok.save_pretrained(str(DIR_MODELO))

    fichero_tok = DIR_MODELO / "tokenizer.json"
    if not fichero_tok.exists():
        raise SystemExit(
            "tokenizer.json was not written: without it Tokenizers.DotNet has nothing to read"
        )

    # The normaliser is the usual cause of divergence: recording it saves hours
    # of debugging if the ids move tomorrow.
    cfg = json.loads(fichero_tok.read_text(encoding="utf-8"))
    normalizador = cfg.get("normalizer")
    pretokenizador = cfg.get("pre_tokenizer")

    items = []
    for entrada in corpus:
        texto = entrada["text"]
        con = tok(texto, add_special_tokens=True)["input_ids"]
        sin = tok(texto, add_special_tokens=False)["input_ids"]
        items.append(
            {
                "id": entrada["id"],
                "text": texto,
                "idsWithSpecial": con,
                "idsNoSpecial": sin,
                "tokensNoSpecial": tok.convert_ids_to_tokens(sin),
            }
        )

    output = {
        "model": MODELO,
        "tokenizerClass": type(tok).__name__,
        "tokenizerJsonSha256": sha256(fichero_tok),
        "vocabSize": tok.vocab_size,
        "specialTokens": {
            "bos": [tok.bos_token, tok.bos_token_id],
            "eos": [tok.eos_token, tok.eos_token_id],
            "unk": [tok.unk_token, tok.unk_token_id],
            "pad": [tok.pad_token, tok.pad_token_id],
        },
        "normalizerType": (normalizador or {}).get("type"),
        "preTokenizerType": (pretokenizador or {}).get("type"),
        "transformersVersion": __import__("transformers").__version__,
        "tokenizersVersion": __import__("tokenizers").__version__,
        "items": items,
    }

    destination = AQUI / "reference_ids.json"
    destination.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")

    total = sum(len(i["idsNoSpecial"]) for i in items)
    print(f"tokenizer: {type(tok).__name__} (vocab {tok.vocab_size})")
    print(f"normaliser: {output['normalizerType']} / pre: {output['preTokenizerType']}")
    print(f"specials: bos={output['specialTokens']['bos']} eos={output['specialTokens']['eos']}")
    print(f"sha256(tokenizer.json): {output['tokenizerJsonSha256'][:16]}...")
    print(f"reference_ids.json: {len(items)} texts, {total} tokens without specials")


if __name__ == "__main__":
    main()
