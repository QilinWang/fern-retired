import fern_scheduler/retry.{
  AcceptedCell, Active, AllowedAfterEnd, AllowedClosingForced,
  AllowedClosingSilent, Cancelled, CancelledStudy, Cell, Ended, MissingCell,
  Observation, Opened, RecentProgress, Refused, Running, UnattemptedCell,
  UnknownSilence,
}
import fern_scheduler/wire
import gleam/option.{None, Some}

pub fn refusal_order_is_preserved_test() {
  assert retry.decide(Observation(Cancelled, None, None, 10, False))
    == Refused(CancelledStudy)
  assert retry.decide(Observation(Active, None, None, 10, True))
    == Refused(MissingCell)
  assert retry.decide(Observation(
      Active,
      Some(Cell(Some(1), None, Opened)),
      None,
      10,
      True,
    ))
    == Refused(AcceptedCell)
  assert retry.decide(Observation(
      Active,
      Some(Cell(None, None, Ended)),
      None,
      10,
      True,
    ))
    == Refused(UnattemptedCell)
}

pub fn a_terminal_attempt_needs_no_silence_or_force_test() {
  assert retry.decide(Observation(
      Active,
      Some(Cell(None, Some(1), Ended)),
      None,
      10,
      True,
    ))
    == AllowedAfterEnd
}

pub fn silence_is_compared_without_rounding_test() {
  let cell = Some(Cell(None, Some(1), Running))
  assert retry.decide(Observation(Active, cell, Some(9), 10, False))
    == Refused(RecentProgress)
  assert retry.decide(Observation(Active, cell, Some(10), 10, False))
    == AllowedClosingSilent
  assert retry.decide(Observation(Active, cell, Some(11), 10, False))
    == AllowedClosingSilent
}

pub fn unknown_silence_requires_an_explicit_override_test() {
  let cell = Some(Cell(None, Some(1), Opened))
  assert retry.decide(Observation(Active, cell, None, 10, False))
    == Refused(UnknownSilence)
  assert retry.decide(Observation(Active, cell, None, 10, True))
    == AllowedClosingForced
}

pub fn only_an_overridden_bound_is_recorded_as_forced_test() {
  let cell = Some(Cell(None, Some(1), Opened))
  assert retry.decide(Observation(Active, cell, Some(9), 10, True))
    == AllowedClosingForced
  assert retry.decide(Observation(Active, cell, Some(10), 10, True))
    == AllowedClosingSilent
}

pub fn the_wire_keeps_the_complete_typed_result_test() {
  let assert Ok(answer) =
    wire.answer(
      "{\"id\":\"r1\",\"ask\":\"retry_cell\",\"study_state\":\"run\",\"cell\":{\"accepted\":null,\"latest\":1,\"state\":\"opened\"},\"silent_ticks\":null,\"stale_ticks\":10,\"force\":false}",
    )
  assert answer
    == "{\"id\":\"r1\",\"answer\":{\"kind\":\"refused\",\"reason\":\"unknown_silence\"}}"
  let assert Ok(answer) =
    wire.answer(
      "{\"id\":\"r2\",\"ask\":\"retry_cell\",\"study_state\":\"run\",\"cell\":{\"accepted\":null,\"latest\":1,\"state\":\"opened\"},\"silent_ticks\":10,\"stale_ticks\":10,\"force\":false}",
    )
  assert answer
    == "{\"id\":\"r2\",\"answer\":{\"kind\":\"allowed\",\"closing\":\"silent\"}}"
}
