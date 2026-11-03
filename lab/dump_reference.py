# -*- coding: utf-8 -*-
"""Referencia de tokenizacion en Python para el hito 0 (BACKEND.md §10).

Cuaderno de laboratorio: NO se distribuye. El producto es .NET puro.

Descarga el tokenizer de multilingual-e5-small UNA vez a backend/models/ y
tokeniza el corpus. C# despues lee EXACTAMENTE ese mismo tokenizer.json: si cada
lado se trajera el suyo, la prueba no probaria nada.

Volcamos los IDs con y sin tokens especiales. Si Tokenizers.DotNet discrepa, hay
que poder distinguir "pone los especiales en otro sitio" (trivial) de "segmenta
el texto distinto" (mortal: los embeddings se degradan en silencio).

Salida: reference_ids.json
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
            "no se ha guardado tokenizer.json: sin el, Tokenizers.DotNet no tiene nada que leer"
        )

    # El normalizador es la causa habitual de divergencia: dejarlo registrado
    # ahorra horas de depuracion si manana los IDs se mueven.
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

    salida = {
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

    destino = AQUI / "reference_ids.json"
    destino.write_text(json.dumps(salida, ensure_ascii=False, indent=2), encoding="utf-8")

    total = sum(len(i["idsNoSpecial"]) for i in items)
    print(f"tokenizer: {type(tok).__name__} (vocab {tok.vocab_size})")
    print(f"normalizador: {salida['normalizerType']} / pre: {salida['preTokenizerType']}")
    print(f"especiales: bos={salida['specialTokens']['bos']} eos={salida['specialTokens']['eos']}")
    print(f"sha256(tokenizer.json): {salida['tokenizerJsonSha256'][:16]}...")
    print(f"reference_ids.json: {len(items)} textos, {total} tokens sin especiales")


if __name__ == "__main__":
    main()
