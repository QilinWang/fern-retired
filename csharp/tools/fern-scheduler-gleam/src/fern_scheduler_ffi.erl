%% Read one line of this program's standard input, which Gleam's libraries do
%% not.
%%
%% One line is one ask. The program is held open by the scheduler that started
%% it and answers line by line for as long as that scheduler runs, so what is
%% needed is a line at a time and an honest end of input — not the whole of
%% standard input, which would only ever return when the caller went away.
-module(fern_scheduler_ffi).
-export([read_line/0]).

read_line() ->
    _ = io:setopts(standard_io, [binary]),
    case io:get_line(standard_io, <<"">>) of
        eof -> {error, nil};
        {error, _} -> {error, nil};
        Line when is_binary(Line) -> {ok, Line};
        Line -> {ok, unicode:characters_to_binary(Line)}
    end.
