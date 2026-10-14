"""Pure checkpoint-selection helpers shared by training and regression tests."""

def is_better_checkpoint(false_grids, grid_correct, val_loss,
                         best_false_grids, best_grid_correct, best_loss):
    """Prefer deletion safety, then grid recall, then validation loss."""
    return (false_grids, -grid_correct, val_loss) < (
        best_false_grids, -best_grid_correct, best_loss)


def should_stop(epochs_without_improvement, patience):
    """A non-positive patience disables early stopping."""
    return patience > 0 and epochs_without_improvement >= patience
