# Skill: project memory

1. The project memory block of this prompt (if any) holds the rules of this project. A rule in the task beats the memory; the memory beats your guess. No block means the memory is empty: do not call `memory_read`.
2. Near the end, call `memory_add` once per fact that will still be true next time, one short sentence (a label rule or a known defect type). Never save coordinates, counts, file paths, secrets, personal data, what the memory or the task already says, or guesses.
