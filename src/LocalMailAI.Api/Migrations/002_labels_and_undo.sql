-- 002: escritura de etiquetas en lote y deshacer.
--
-- Dos cosas que el esquema de §4 no podia expresar y que el flujo acordado con el
-- front necesita.

-- 1. Que se ha escrito ya en el servidor IMAP.
--
-- Sin esto no hay forma de contestar a "cuantas etiquetas quedan por aplicar": en
-- `corrections` esta el evento y en `classifications` el estado actual, pero ninguna
-- de las dos sabe que llego al servidor. El boton "Aplicar N etiquetas" mentiria, o
-- reescribiriamos etiquetas ya aplicadas en cada pasada.
--
-- Pendiente = correos cuyo bucket actual no coincide con el ultimo escrito.
CREATE TABLE label_writes (
  email_id TEXT PRIMARY KEY REFERENCES emails(id) ON DELETE CASCADE,
  folder TEXT NOT NULL,           -- carpeta/etiqueta IMAP real, p.ej. 'LocalMail/Responder'
  bucket TEXT NOT NULL,           -- bucket que se escribio, para comparar con el actual
  written_at TEXT NOT NULL
);

-- 2. La confianza anterior a una correccion, para poder deshacer de verdad.
--
-- Una correccion del usuario pone la confianza a 1.0: lo ha dicho el usuario, no hay
-- duda. Pero al deshacer hay que devolver el correo a donde estaba, y eso incluye su
-- confianza original; si no, un correo que estaba en "No estoy seguro" reapareceria
-- afirmado en un bucket despues de deshacer, que es lo contrario de deshacer.
--
-- No se puede recalcular: `signals_json` guarda la etiqueta y el signo de cada senal,
-- no su peso, asi que la puntuacion original no es reconstruible desde la fila.
ALTER TABLE corrections ADD COLUMN confidence_before REAL;

-- Indice para el deshacer: se recorre el registro de la mas reciente hacia atras.
CREATE INDEX idx_corrections_recientes ON corrections(id DESC);
