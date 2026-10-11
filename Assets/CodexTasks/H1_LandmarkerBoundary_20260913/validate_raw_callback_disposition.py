#!/usr/bin/env python3
"""H1-only raw callback disposition validator. Independent of Unity/Products.
Usage: python validate_raw_callback_disposition.py path/to/raw_callback_disposition.csv path/to/summary.txt
"""
import csv
import json
import sys
from collections import Counter
from pathlib import Path

FIELDS = (
    "sessionId rawSequence callbackTimestamp submissionHostTicks arrivalHostTicks cameraGeneration "
    "trackingSessionGeneration providerGeneration submittedSourceSequence submittedSourceSequenceAvailability "
    "faceLandmarksNull faceCount firstLandmarksNull firstLandmarkCount observerValid455 storeInputNonEmpty "
    "storeCalled storeReturned storeOutcome publicationOccurred publicationPrecisionValid publishedBackend "
    "publishedFrameId publishedTimestamp recordCompletionStatus"
).split()
ACCEPTED = {
    "NO_FACE_CLEAR_ACCEPTED_RESET", "NO_FACE_CLEAR_ACCEPTED_PRIMARY_PRESERVED",
    "AUXILIARY_ACCEPTED", "MEDIAPIPE_SNAPSHOT_WRITTEN_VALID",
    "MEDIAPIPE_SNAPSHOT_WRITTEN_INVALID_PRECISION",
}
PUBLISHED = {"MEDIAPIPE_SNAPSHOT_WRITTEN_VALID", "MEDIAPIPE_SNAPSHOT_WRITTEN_INVALID_PRECISION"}
REJECTED = {"REJECTED_GATE", "REJECTED_STALE"}
OUTCOMES = ACCEPTED | REJECTED
UNAVAILABLE = {"UNAVAILABLE"}
def required_int(row, key):
    return int(row[key])

def validate(csv_path, summary_path):
    errors = []
    reasons = Counter()
    session = None
    last_sequence = 0
    last_timestamp = None
    duplicates = out_of_order = 0
    count = 0
    with csv_path.open("r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        if reader.fieldnames != FIELDS:
            errors.append("CSV_SCHEMA_MISMATCH")
            return {"result": "FAIL", "errors": errors}
        for row in reader:
            count += 1
            def fail(reason):
                if len(errors) < 50:
                    errors.append(f"row={count}:{reason}")
            if None in row or any(value is None for value in row.values()):
                fail("CSV_ROW_CARDINALITY")
                continue
            if not row["sessionId"]:
                fail("MISSING_SESSION_ID")
            if session is None:
                session = row["sessionId"]
            elif row["sessionId"] != session:
                fail("MIXED_SESSION")
            try:
                sequence = required_int(row, "rawSequence")
                timestamp = required_int(row, "callbackTimestamp")
                host = required_int(row, "submissionHostTicks")
                arrival = required_int(row, "arrivalHostTicks")
                camera_generation = required_int(row, "cameraGeneration")
                face_count = required_int(row, "faceCount")
                first_null = required_int(row, "firstLandmarksNull")
                first_count = required_int(row, "firstLandmarkCount")
                frame_id = required_int(row, "publishedFrameId")
                published_timestamp = required_int(row, "publishedTimestamp")
            except (ValueError, TypeError) as e:
                fail("INVALID_INTEGER:" + str(e))
                continue
            if sequence != last_sequence + 1:
                fail("RAW_SEQUENCE_GAP_OR_DUPLICATE")
            last_sequence = sequence
            if last_timestamp is not None:
                if timestamp == last_timestamp:
                    duplicates += 1
                if timestamp < last_timestamp:
                    out_of_order += 1
            last_timestamp = timestamp
            if timestamp < 0 or host <= 0 or arrival <= 0 or camera_generation <= 0:
                fail("INVALID_TIMING_OR_GENERATION")
            if (row["trackingSessionGeneration"] not in UNAVAILABLE or
                row["providerGeneration"] not in UNAVAILABLE or
                row["submittedSourceSequence"] not in UNAVAILABLE or
                row["submittedSourceSequenceAvailability"] not in UNAVAILABLE):
                fail("UNPROVEN_EXACT_SOURCE_IDENTITY")
            faces_null = row["faceLandmarksNull"] == "1"
            if row["faceLandmarksNull"] not in ("0", "1"):
                fail("INVALID_NULL_FLAG")
            if faces_null != (face_count == -1):
                fail("FACE_COUNT_NULL_INCONSISTENT")
            if face_count <= 0 and (first_null != -1 or first_count != -1):
                fail("UNAVAILABLE_FIRST_FACE_NOT_MARKED")
            if first_null not in (-1, 0, 1):
                fail("INVALID_FIRST_LANDMARK_NULL_FLAG")
            if face_count > 0 and (first_null == -1 or ((first_null == 1) != (first_count == -1))):
                fail("FIRST_LANDMARK_NULL_INCONSISTENT")
            if row["observerValid455"] != ("1" if first_count >= 455 else "0"):
                fail("OBSERVER_VALIDITY_MISMATCH")
            if row["storeInputNonEmpty"] != ("1" if first_count > 0 else "0"):
                fail("STORE_INPUT_NONEMPTY_MISMATCH")
            outcome = row["storeOutcome"]
            reasons[outcome] += 1
            if row["recordCompletionStatus"] != "COMPLETE":
                fail("INCOMPLETE_CALLBACK_RECORD")
            if row["storeCalled"] != "1" or outcome not in OUTCOMES:
                fail("UNKNOWN_OR_NOT_CALLED")
            if row["storeReturned"] != ("1" if outcome in ACCEPTED else "0"):
                fail("STORE_RETURN_OUTCOME_MISMATCH")
            published = outcome in PUBLISHED
            if row["publicationOccurred"] != ("1" if published else "0"):
                fail("PUBLICATION_OUTCOME_MISMATCH")
            if published:
                if first_count <= 0 or row["publishedBackend"] != "MediaPipe" or frame_id <= 0:
                    fail("INVALID_PUBLICATION_IDENTITY")
                if published_timestamp != timestamp:
                    fail("PUBLICATION_TIMESTAMP_MISMATCH")
                valid = outcome == "MEDIAPIPE_SNAPSHOT_WRITTEN_VALID"
                if row["publicationPrecisionValid"] != ("1" if valid else "0"):
                    fail("PUBLICATION_PRECISION_MISMATCH")
            else:
                if row["publicationPrecisionValid"] != "0" or frame_id != 0 or published_timestamp != 0:
                    fail("NONPUBLISHED_IDENTITY_NOT_EMPTY")
            if outcome.startswith("NO_FACE") and first_count > 0:
                fail("NO_FACE_BRANCH_WITH_NONEMPTY_FIRST_LANDMARKS")
            if outcome in PUBLISHED | {"AUXILIARY_ACCEPTED"} and first_count <= 0:
                fail("NONEMPTY_BRANCH_WITHOUT_LANDMARKS")
    status_line = None
    for line in summary_path.read_text(encoding="utf-8-sig").splitlines():
        if line.startswith("RAW_CALLBACK_DISPOSITION_STATUS="):
            status_line = line.split("=", 1)[1]
    if status_line is None:
        errors.append("SUMMARY_STATUS_MISSING")
        status = {}
    else:
        try:
            status = {k: int(v) for k, v in (p.split("=", 1) for p in status_line.split(","))}
        except (ValueError, TypeError):
            status = {}
            errors.append("SUMMARY_STATUS_INVALID")
    if count == 0:
        errors.append("ZERO_ELIGIBLE_CALLBACK_COVERAGE")
    if status.get("produced") != count or status.get("written") != count:
        errors.append("RAW_CALLBACK_CARDINALITY_MISMATCH")
    if any(status.get(key, -1) != 0 for key in ("overflow", "missing", "pending", "writeFault")):
        errors.append("DIAGNOSTIC_COVERAGE_OR_WRITE_FAULT")
    if status.get("produced") != last_sequence:
        errors.append("LAST_RAW_SEQUENCE_MISMATCH")
    return {
        "result": "PASS_SCOPE_LIMITED_TO_CALLBACK_PAYLOAD_AND_STORE" if not errors else "FAIL",
        "eligible_count": count,
        "session_id": session,
        "outcomes": dict(reasons),
        "duplicate_callback_timestamps": duplicates,
        "out_of_order_callback_timestamps": out_of_order,
        "missing_source_sequence_identity": True,
        "errors": errors,
    }

if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: validate_raw_callback_disposition.py CALLBACK_CSV SUMMARY_TXT", file=sys.stderr)
        sys.exit(2)
    try:
        result = validate(Path(sys.argv[1]), Path(sys.argv[2]))
    except (OSError, UnicodeError, csv.Error) as error:
        result = {"result": "FAIL", "errors": ["DATA_READ_ERROR:" + str(error)]}
    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    sys.exit(0 if result["result"].startswith("PASS_") else 1)
