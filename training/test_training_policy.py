import unittest

from training_policy import is_better_checkpoint, should_stop


class TrainingPolicyTests(unittest.TestCase):
    def test_fewer_false_grids_wins(self):
        self.assertTrue(is_better_checkpoint(0, 35, 0.2, 1, 40, 0.01))

    def test_more_grid_recall_wins_when_safety_ties(self):
        self.assertTrue(is_better_checkpoint(0, 39, 0.2, 0, 38, 0.01))

    def test_lower_loss_breaks_a_safety_and_recall_tie(self):
        self.assertTrue(is_better_checkpoint(0, 39, 0.01, 0, 39, 0.02))
        self.assertFalse(is_better_checkpoint(0, 39, 0.03, 0, 39, 0.02))

    def test_patience_three_stops_after_three_non_improving_epochs(self):
        self.assertFalse(should_stop(2, 3))
        self.assertTrue(should_stop(3, 3))


if __name__ == "__main__":
    unittest.main()
