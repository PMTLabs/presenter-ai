import { useParams } from 'react-router-dom';

export function Present() {
  const { id } = useParams<{ id: string }>();
  return <p className="text-gray-600 dark:text-gray-400">Presenter for <strong>{id}</strong> is coming in the next phase.</p>;
}
