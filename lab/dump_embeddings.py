# -*- coding: utf-8 -*-
"""Referencia de embeddings para el hito 6.

Cuaderno de laboratorio: NO se distribuye. El producto es .NET puro.

El hito 0 ya demostro que la tokenizacion coincide. Lo que falta por verificar es el
POST-PROCESADO, que es donde se pierden los embeddings sin que nadie se entere:

  - mean pooling PONDERADO por la mascara de atencion, no media simple. Promediar
    tambien las posiciones de relleno mete ruido proporcional a cuanto relleno haya,
    asi que el mismo texto daria vectores distintos segun el tamano del lote.
  - normalizacion L2 al final. Sin ella, el coseno deja de ser comparable entre
    vectores y el ranking de la busqueda sale mal ordenado.
  - los prefijos de e5: "query: " para la consulta y "passage: " para los documentos.
    Olvidarlos degrada los resultados EN SILENCIO (§8), que es el peor modo de fallo.

Nada de esto da error si se hace mal: da vectores plausibles y peores. Por eso se
compara contra una referencia en vez de confiar en que esta bien.

Salida: reference_embeddings.json con los vectores de unos pocos textos.
"""
import json
import pathlib

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

AQUI = pathlib.Path(__file__).resolve().parent
DIR_MODELO = AQUI.parent / "models" / "multilingual-e5-small"

# Textos elegidos para que la comparacion sea informativa, no para que sea facil:
# acentos, un par query/passage del mismo contenido, un texto largo que obligue a
# truncar, uno muy corto, y dos que deberian parecerse mucho entre si.
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
    fichero = DIR_MODELO / "onnx" / "model.onnx"
    if not fichero.exists():
        raise SystemExit(f"falta {fichero}: descarga el modelo ONNX primero")

    tok = AutoTokenizer.from_pretrained(str(DIR_MODELO))
    sesion = ort.InferenceSession(str(fichero), providers=["CPUExecutionProvider"])

    entradas = [e.name for e in sesion.get_inputs()]
    print("entradas del modelo:", entradas)
    print("salidas del modelo :", [s.name for s in sesion.get_outputs()])

    items = []

    for texto in TEXTOS:
        # 512 es el maximo de e5-small. Truncar aqui y no antes: el recorte va en
        # tokens, no en caracteres.
        cod = tok(texto, padding=False, truncation=True, max_length=512, return_tensors="np")

        alimentacion = {"input_ids": cod["input_ids"].astype(np.int64),
                        "attention_mask": cod["attention_mask"].astype(np.int64)}
        if "token_type_ids" in entradas:
            alimentacion["token_type_ids"] = np.zeros_like(cod["input_ids"], dtype=np.int64)

        ultima_capa = sesion.run(None, alimentacion)[0]        # (1, tokens, 384)
        mascara = cod["attention_mask"].astype(np.float32)      # (1, tokens)

        # Mean pooling ponderado por la mascara.
        sumado = (ultima_capa * mascara[..., None]).sum(axis=1)
        vector = (sumado / np.maximum(mascara.sum(axis=1, keepdims=True), 1e-9))[0]

        # L2.
        vector = vector / max(float(np.linalg.norm(vector)), 1e-12)

        items.append({
            "text": texto,
            "tokens": int(cod["attention_mask"].sum()),
            "dim": int(vector.shape[0]),
            # 8 decimales: de sobra para detectar una divergencia real y no tanto
            # como para que el fichero sea ilegible.
            "vector": [round(float(v), 8) for v in vector],
        })

        print(f"  {cod['attention_mask'].sum():4d} tokens  norma={np.linalg.norm(vector):.6f}  {texto[:54]}")

    # Pares que deberian parecerse: sirve para que el lado C# pueda comprobar no solo
    # los numeros sino que el modelo hace lo que tiene que hacer.
    def cos(a, b):
        va, vb = np.array(items[a]["vector"]), np.array(items[b]["vector"])
        return float(va @ vb)

    parecidos = {
        "reunion_query_vs_passage": cos(1, 0),
        "factura_query_vs_passage": cos(3, 2),
        "factura_query_vs_newsletter": cos(3, 4),
    }

    print()
    for k, v in parecidos.items():
        print(f"  coseno {k}: {v:.4f}")

    salida = {
        "model": "intfloat/multilingual-e5-small",
        "onnxruntimeVersion": ort.__version__,
        "pooling": "mean ponderado por attention_mask, luego L2",
        "maxLength": 512,
        "expectedCosines": parecidos,
        "items": items,
    }

    (AQUI / "reference_embeddings.json").write_text(
        json.dumps(salida, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"\nreference_embeddings.json: {len(items)} vectores de dimension {items[0]['dim']}")


if __name__ == "__main__":
    main()
