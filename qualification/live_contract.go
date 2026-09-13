package qualification

// Lookup intentionally demonstrates a reviewable bug: callers expect a missing
// key to be distinguishable from a stored empty value, but this function loses
// that distinction.
func Lookup(values map[string]string, key string) string { return values[key] }
