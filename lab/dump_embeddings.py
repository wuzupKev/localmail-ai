# -*- coding: utf-8 -*-
"""Embedding reference for milestone 6.

Lab notebook: NOT shipped. The product is pure .NET.

Milestone 0 already proved tokenisation matches. What remains to verify is the
POST-PROCESSING, which is where embeddings are lost without anyone noticing:

  - mean pooling WEIGHTED by the attention mask, not a plain average. Averaging
    the padding positions too injects noise proportional to how much padding
    there is, so the same text would produce different vectors depending on the
    batch it lands in.
  - L2 normalisation at the end. Without it the cosine stops being comparable
    between vectors and search ranking comes out in the wrong order.
  - the e5 prefixes: "query: " for the query and "passage: " for documents.
    Omitting them degrades results SILENTLY (§8), the worst failure mode.

None of this raises an error when done wrong: it produces plausible, worse
vectors. Hence comparing against a reference rather than trusting it is right.

Output: reference_embeddings.json with the vectors of a handful of texts.
"""
import json
import pathlib

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

AQUI = pathlib.Path(__file__).resolve().parent
DIR_MODELO = AQUI.parent / "models" / "multilingual-e5-small"

# Texts chosen so the comparison is informative rather than easy: accents, a
# query/passage pair over the same content, a long text that forces truncation, a
# very short one, and two that should be highly similar to each other.
TEXTOS = [
    "passage: Te viene bien a las 10 en lugar de las 9? Me ha surgido una vista a primera hora.",
    "query: cambio de hora de la reunion del jueves",
    "passage: Le recordamos que la factura F-2291 por importe de 348,00 EUR vence el 10/11/2026.",
    "query: factura F-2291 vencimiento",
    "passage: Descubre las herramientas que estan cambiando la forma de trabajar.",
    "passage: Anado a Begona y a Inigo en copia. La reunion sera el miercoles a las 9:30.",
    "passage: a",
    "passage: " + " ".join(["palabra"] * 400),
]


def main() -> None:
    path = DIR_MODELO / "onnx" / "model.onnx"
    if not path.exists():
        raise SystemExit(f"{path} is missing: download the ONNX model first")

    tok = AutoTokenizer.from_pretrained(str(DIR_MODELO))
    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])

    inputs = [e.name for e in session.get_inputs()]
    print("model inputs :", inputs)
    print("model outputs:", [s.name for s in session.get_outputs()])

    items = []

    for texto in TEXTOS:
        # 512 is the e5-small maximum. Truncate here and not earlier: the cut is
        # in tokens, not characters.
        cod = tok(texto, padding=False, truncation=True, max_length=512, return_tensors="np")

        feed = {"input_ids": cod["input_ids"].astype(np.int64),
                        "attention_mask": cod["attention_mask"].astype(np.int64)}
        if "token_type_ids" in inputs:
            feed["token_type_ids"] = np.zeros_like(cod["input_ids"], dtype=np.int64)

        last_hidden = session.run(None, feed)[0]        # (1, tokens, 384)
        mask = cod["attention_mask"].astype(np.float32)      # (1, tokens)

        # Mask-weighted mean pooling.
        summed = (last_hidden * mask[..., None]).sum(axis=1)
        vector = (summed / np.maximum(mask.sum(axis=1, keepdims=True), 1e-9))[0]

        # L2.
        vector = vector / max(float(np.linalg.norm(vector)), 1e-12)

        items.append({
            "text": texto,
            "tokens": int(cod["attention_mask"].sum()),
            "dim": int(vector.shape[0]),
            # 8 decimals: plenty to catch a real divergence, not so many that the
            # file becomes unreadable.
            "vector": [round(float(v), 8) for v in vector],
        })

        print(f"  {cod['attention_mask'].sum():4d} tokens  norm={np.linalg.norm(vector):.6f}  {texto[:54]}")

    # Pairs that should be similar: this lets the C# side check not just the
    # numbers but that the model is doing what it should.
    def cos(a, b):
        va, vb = np.array(items[a]["vector"]), np.array(items[b]["vector"])
        return float(va @ vb)

    similarities = {
        "reunion_query_vs_passage": cos(1, 0),
        "factura_query_vs_passage": cos(3, 2),
        "factura_query_vs_newsletter": cos(3, 4),
    }

    print()
    for k, v in similarities.items():
        print(f"  cosine {k}: {v:.4f}")

    output = {
        "model": "intfloat/multilingual-e5-small",
        "onnxruntimeVersion": ort.__version__,
        "pooling": "mask-weighted mean, then L2",
        "maxLength": 512,
        "expectedCosines": similarities,
        "items": items,
    }

    (AQUI / "reference_embeddings.json").write_text(
        json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"\nreference_embeddings.json: {len(items)} vectors of dimension {items[0]['dim']}")


if __name__ == "__main__":
    main()
