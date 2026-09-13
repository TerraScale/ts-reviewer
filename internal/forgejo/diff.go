package forgejo

import (
	"strconv"
	"strings"
)

// ParseUnifiedDiff converts Forgejo's raw unified diff into file patches and
// tracks the review position expected by CreatePullReviewComment.
func ParseUnifiedDiff(raw string) []File {
	var files []File
	var current *File
	for _, line := range strings.Split(raw, "\n") {
		if strings.HasPrefix(line, "diff --git a/") {
			parts := strings.Split(line, " b/")
			if len(parts) == 2 {
				files = append(files, File{Filename: parts[1]})
				current = &files[len(files)-1]
				current.Patch = line + "\n"
			}
			continue
		}
		if current != nil {
			current.Patch += line + "\n"
		}
	}
	return files
}

// ReviewPosition returns the Forgejo new_position for a changed line.
func ReviewPosition(patch string, target int) int {
	if target <= 0 {
		return 0
	}
	newLine, position := 0, 0
	for _, line := range strings.Split(patch, "\n") {
		if strings.HasPrefix(line, "@@ ") {
			fields := strings.Fields(line)
			for _, field := range fields {
				if strings.HasPrefix(field, "+") {
					field = strings.TrimPrefix(field, "+")
					field = strings.SplitN(field, ",", 2)[0]
					if n, err := strconv.Atoi(field); err == nil {
						newLine = n
					}
				}
			}
			continue
		}
		if strings.HasPrefix(line, "+++") || strings.HasPrefix(line, "---") || line == "" {
			continue
		}
		if strings.HasPrefix(line, "-") {
			position++
			continue
		}
		if strings.HasPrefix(line, "+") || strings.HasPrefix(line, " ") {
			position++
			if newLine == target {
				return position
			}
			newLine++
		}
	}
	return 0
}
