    # Read-only C# admission of the new coordinate shape. No plan is registered
    # and no training or Registry write is performed by --admit-only.
    import subprocess

    from fern_train.worker import fern_run_csharp_cmd

    path = tmp_path / "compiled-study.json"
    path.write_text(json.dumps(compiled))
    admitted = subprocess.run([*fern_run_csharp_cmd(), "run-study", "--admit-only",
                               "--from", str(path), "--on", "local-4070"],
                              capture_output=True, text=True, check=False)
    assert admitted.returncode == 0, admitted.stdout + admitted.stderr
    assert "ADMITTED: study" in admitted.stdout
