import fern_scheduler/wire

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
