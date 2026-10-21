"""Pinned offline Gemma Q4 adapter. Heavy dependencies load only on first use."""

import hashlib
import json
from pathlib import Path
import threading
import unicodedata

RESOURCE_SPEC = json.loads(Path(__file__).with_name("builtin_embedding_resources.json").read_text(encoding="utf-8"))
MODEL = RESOURCE_SPEC["model"]
REVISION = RESOURCE_SPEC["revision"]


class BuiltinEmbeddingError(ValueError):
    """Only a stable code, never runtime details or source text, leaves the adapter."""

    def __init__(self, code):
        self.code = code
        super().__init__(code)


class BuiltinEmbeddingProvider:
    dimensions = 768

    def __init__(self, resource_directory, *, model=MODEL, model_version=None, dimensions=None):
        if model != MODEL or model_version not in (None, REVISION) or dimensions not in (None, 768):
            raise BuiltinEmbeddingError("BUILTIN_EMBEDDING_VERSION_INVALID")
        self.model = model
        self.resource_directory = Path(resource_directory)
        if not self.resource_directory.is_absolute():
            raise BuiltinEmbeddingError("BUILTIN_EMBEDDING_DIRECTORY_INVALID")
        self._lock = threading.RLock()
        self._session = self._tokenizer = None
        self.provider_status, self.error_code = "unloaded", None

    @property
    def model_key(self):
        from .canonical_embeddings import TEXT_VERSION
        identity = dict(provider="builtin", **RESOURCE_SPEC, text_version=TEXT_VERSION,
                        encoding="search-result-query/none-title-document/projected-sentence/l2/1")
        return "space_v2:" + hashlib.sha256(json.dumps(identity, sort_keys=True,
            separators=(",", ":")).encode()).hexdigest()

    def _fail(self, code):
        self.provider_status, self.error_code = "failed", code
        raise BuiltinEmbeddingError(code) from None

    def _load(self):
        if self.provider_status == "closed":
            raise BuiltinEmbeddingError("BUILTIN_EMBEDDING_CLOSED")
        if self._session is not None:
            return
        for relative, expected in RESOURCE_SPEC["files"].items():
            try:
                with (self.resource_directory / relative).open("rb") as source:
                    digest = hashlib.file_digest(source, "sha256").hexdigest()
            except OSError:
                self._fail("BUILTIN_EMBEDDING_FILE_MISSING")
            if digest != expected:
                self._fail("BUILTIN_EMBEDDING_SHA_MISMATCH")
        try:
            from tokenizers import Tokenizer
            tokenizer = Tokenizer.from_file(str(self.resource_directory / "tokenizer.json"))
            # Never silently truncate a Canonical Memory or query.
            tokenizer.no_truncation()
            tokenizer.no_padding()
        except Exception:
            self._fail("BUILTIN_EMBEDDING_TOKENIZER_INIT_FAILED")
        try:
            import onnxruntime as ort
            ort.disable_telemetry_events()
            options = ort.SessionOptions()
            options.intra_op_num_threads, options.inter_op_num_threads = 4, 1
            options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
            options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
            session = ort.InferenceSession(str(self.resource_directory / "onnx/model_q4.onnx"),
                sess_options=options, providers=["CPUExecutionProvider"])
            session.disable_fallback()
            if (session.get_providers() != ["CPUExecutionProvider"] or
                    {i.name for i in session.get_inputs()} != {"input_ids", "attention_mask"} or
                    "sentence_embedding" not in {o.name for o in session.get_outputs()}):
                raise ValueError
        except Exception:
            self._fail("BUILTIN_EMBEDDING_ONNX_INIT_FAILED")
        self._tokenizer, self._session = tokenizer, session
        self.provider_status, self.error_code = "ready", None

    def embed(self, text):
        """Canonical Indexer contract: encode a document, with its document task."""
        return self._encode(text, query=False)

    def embed_query(self, text):
        return self._encode(text, query=True)

    def _encode(self, text, *, query):
        # Index workers and foreground Recall share this lock, including tokenizer state.
        with self._lock:
            self._load()
            import numpy as np
            try:
                text = unicodedata.normalize("NFKC", text).strip()
                prefixed = ("task: search result | query: " if query else "title: none | text: ") + text
                encoding = self._tokenizer.encode(prefixed, add_special_tokens=True)
                if len(encoding.ids) > RESOURCE_SPEC["max_tokens"]:
                    raise BuiltinEmbeddingError("BUILTIN_EMBEDDING_INPUT_TOO_LONG")
                result = self._session.run(["sentence_embedding"], {
                    "input_ids": np.array([encoding.ids], dtype=np.int64),
                    "attention_mask": np.array([encoding.attention_mask], dtype=np.int64)})[0]
            except BuiltinEmbeddingError as error:
                self._fail(error.code)
            except Exception:
                self._fail("BUILTIN_EMBEDDING_INFERENCE_FAILED")
            try:
                # The pinned graph already contains mean pooling and learned projection.
                # Re-normalize in float32, exactly as the verified experiment encoder.
                vector = np.asarray(result, dtype=np.float32)
                if vector.shape != (1, 768) or not np.isfinite(vector).all():
                    raise ValueError
                vector = vector[0]
                norm = np.linalg.norm(vector)
                if not np.isfinite(norm) or norm <= 0:
                    raise ValueError
                vector = vector / norm
            except Exception:
                self._fail("BUILTIN_EMBEDDING_VECTOR_INVALID")
            self.provider_status, self.error_code = "ready", None
            return tuple(float(value) for value in vector)

    def close(self):
        with self._lock:
            self._session = self._tokenizer = None
            self.provider_status = "closed"
